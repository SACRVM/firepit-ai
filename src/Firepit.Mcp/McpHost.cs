using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Serilog;

namespace Firepit.Mcp;

/// <summary>
/// In-process named-pipe MCP server. firepit-mcp.exe (the stdio bridge)
/// connects here as a pure tunnel; the wire format on the pipe is the same
/// JSON-RPC envelopes Claude Code sends, framed with a 4-byte little-endian
/// length prefix.
/// </summary>
public sealed class McpHost : IDisposable
{
    public const string PipeName    = "firepit-mcp";
    public const int    MaxClients  = 8;
    public const string Protocol    = "2024-11-05";
    public const string ServerName  = "firepit";

    private readonly IMcpBackend _backend;
    private readonly string      _serverVersion;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public McpHost(IMcpBackend backend, string serverVersion = "0.5.0")
    {
        _backend       = backend ?? throw new ArgumentNullException(nameof(backend));
        _serverVersion = serverVersion;
    }

    public void Start()
    {
        for (var i = 0; i < MaxClients; i++)
        {
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        Log.Information("MCP host listening on pipe '{Pipe}' ({Max} concurrent)", PipeName, MaxClients);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignored */ }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                Log.Debug("MCP client connected");
                await HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex)
            {
                Log.Debug(ex, "MCP pipe IO ended (client disconnect or shutdown)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MCP pipe accept-loop error");
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* ignored */ }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var lengthBuf = new byte[4];

        // First frame is the firepit-mcp bridge's context handshake. Captures
        // which project the calling agent is in (used for firepit_send_to's
        // implicit 'from' field).
        var ctx = await ReadContextHandshakeAsync(pipe, lengthBuf, ct);
        Log.Debug("MCP client context: project='{Project}' pid={Pid}", ctx.ProjectName, ctx.Pid);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            if (!await ReadExactlyAsync(pipe, lengthBuf, 0, 4, ct)) return;
            var length = BitConverter.ToInt32(lengthBuf, 0);
            if (length <= 0 || length > 16 * 1024 * 1024) return;
            var payload = new byte[length];
            if (!await ReadExactlyAsync(pipe, payload, 0, length, ct)) return;

            string? responseJson;
            try
            {
                responseJson = await DispatchAsync(payload, ctx);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MCP dispatch error");
                responseJson = BuildErrorResponse(JsonValue.Create((string?)null), -32603, "Internal error: " + ex.Message);
            }

            if (responseJson is null) continue; // notification — no response
            var resp = Encoding.UTF8.GetBytes(responseJson);
            var lb = BitConverter.GetBytes(resp.Length);
            try
            {
                await pipe.WriteAsync(lb, ct);
                await pipe.WriteAsync(resp, ct);
                await pipe.FlushAsync(ct);
            }
            catch (IOException) { return; }
        }
    }

    private static async Task<ConnectionContext> ReadContextHandshakeAsync(
        Stream pipe, byte[] lengthBuf, CancellationToken ct)
    {
        if (!await ReadExactlyAsync(pipe, lengthBuf, 0, 4, ct))
            return ConnectionContext.Empty;
        var length = BitConverter.ToInt32(lengthBuf, 0);
        if (length <= 0 || length > 16 * 1024)
            return ConnectionContext.Empty;
        var payload = new byte[length];
        if (!await ReadExactlyAsync(pipe, payload, 0, length, ct))
            return ConnectionContext.Empty;

        try
        {
            var node = JsonNode.Parse(payload);
            var ctx = node?["_firepit_context"];
            if (ctx is null) return ConnectionContext.Empty;
            return new ConnectionContext(
                ProjectName: ctx["projectName"]?.GetValue<string>() ?? "",
                Pid:         ctx["pid"]?.GetValue<int?>() ?? 0);
        }
        catch
        {
            return ConnectionContext.Empty;
        }
    }

    /// <summary>
    /// Returns the response JSON string, or null for notifications (no response).
    /// </summary>
    private async Task<string?> DispatchAsync(byte[] payload, ConnectionContext ctx)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            return BuildErrorResponse(null, -32700, "Parse error: " + ex.Message);
        }
        if (root is null) return null;

        var idNode = root["id"];
        var method = root["method"]?.GetValue<string>();
        var paramsNode = root["params"];

        if (string.IsNullOrEmpty(method))
        {
            return BuildErrorResponse(idNode, -32600, "Invalid request: missing method");
        }

        // Notifications have no id; they expect no response.
        var isNotification = idNode is null;

        try
        {
            return method switch
            {
                "initialize"                 => BuildResult(idNode, BuildInitializeResult()),
                "notifications/initialized"  => null,
                "tools/list"                 => BuildResult(idNode, BuildToolsList()),
                "tools/call"                 => await HandleToolsCallAsync(idNode, paramsNode, ctx),
                "resources/list"             => BuildResult(idNode, BuildResourcesList()),
                "resources/read"             => await HandleResourcesReadAsync(idNode, paramsNode),
                "ping"                       => BuildResult(idNode, new JsonObject()),
                _ when isNotification        => null,
                _                            => BuildErrorResponse(idNode, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dispatch handler threw for {Method}", method);
            return BuildErrorResponse(idNode, -32603, "Internal error: " + ex.Message);
        }
    }

    // --- initialize -----------------------------------------------------

    private JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = Protocol,
        ["capabilities"] = new JsonObject
        {
            ["tools"]     = new JsonObject(),
            ["resources"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"]    = ServerName,
            ["version"] = _serverVersion,
        },
    };

    // --- tools/list -----------------------------------------------------

    private JsonObject BuildToolsList()
    {
        var tools = new JsonArray();
        foreach (var def in McpToolDefinitions.All)
        {
            tools.Add(new JsonObject
            {
                ["name"]        = def.Name,
                ["description"] = def.Description,
                ["inputSchema"] = JsonNode.Parse(def.InputSchemaJson)!,
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    // --- tools/call -----------------------------------------------------

    private async Task<string> HandleToolsCallAsync(JsonNode? id, JsonNode? paramsNode, ConnectionContext ctx)
    {
        var name = paramsNode?["name"]?.GetValue<string>();
        var args = paramsNode?["arguments"] as JsonObject ?? new JsonObject();

        Log.Information("MCP tools/call: {Name} (from project '{From}')", name, ctx.ProjectName);

        switch (name)
        {
            case "firepit_list_projects":
            {
                var projects = await _backend.ListProjectsAsync();
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(projects, McpJsonContext.Default.IReadOnlyListProjectInfo)));
            }
            case "firepit_create_project":
            {
                var newName = args["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(newName))
                    return BuildErrorResponse(id, -32602, "missing 'name'");
                var newPath = args["path"]?.GetValue<string>();
                var applyBlueprint = args["applyBlueprint"]?.GetValue<bool?>() ?? true;
                var result = await _backend.CreateProjectAsync(
                    newName.Trim(), string.IsNullOrWhiteSpace(newPath) ? null : newPath, applyBlueprint);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.CreateProjectResult)));
            }
            case "firepit_rename_project":
            {
                var to = args["to"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(to))
                    return BuildErrorResponse(id, -32602, "missing 'to'");
                var fromName = args["from"]?.GetValue<string>();
                var fromPath = args["fromPath"]?.GetValue<string>();
                if (fromName is null && string.IsNullOrWhiteSpace(fromPath))
                    return BuildErrorResponse(id, -32602, "missing 'from' (or 'fromPath')");
                var renameFolder   = args["renameFolder"]?.GetValue<bool?>() ?? true;
                var migrateHistory = args["migrateHistory"]?.GetValue<bool?>() ?? true;
                var result = await _backend.RenameProjectAsync(
                    fromName, string.IsNullOrWhiteSpace(fromPath) ? null : fromPath,
                    to.Trim(), renameFolder, migrateHistory);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.RenameProjectResult)));
            }
            case "firepit_open_tab":
            {
                var project = args["projectName"]?.GetValue<string>();
                var resume  = args["resume"]?.GetValue<bool?>() ?? false;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName'");
                var result = await _backend.OpenTabAsync(project, resume);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_focus_tab":
            {
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName'");
                var result = await _backend.FocusTabAsync(project);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_close_tab":
            {
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName'");
                var result = await _backend.CloseTabAsync(project);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_reload":
            {
                var project = args["projectName"]?.GetValue<string>();
                var restart = args["restart"]?.GetValue<bool?>() ?? false;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName'");
                var result = await _backend.ReloadAsync(project, restart);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_inbox_list":
            {
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.ListInboxAsync(project);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.InboxListResult)));
            }
            case "firepit_inbox_complete":
            {
                var messageId = args["id"]?.GetValue<string>();
                var project   = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(messageId))
                    return BuildErrorResponse(id, -32602, "missing 'id'");
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.CompleteInboxAsync(project, messageId);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_send_to":
            {
                var fromProject = !string.IsNullOrEmpty(ctx.ProjectName) ? ctx.ProjectName : null;
                var toProject   = args["toProject"]?.GetValue<string>();
                var subject     = args["subject"]?.GetValue<string>();
                var body        = args["body"]?.GetValue<string>();
                var priority    = args["priority"]?.GetValue<string?>() ?? "normal";
                if (string.IsNullOrEmpty(toProject) || string.IsNullOrEmpty(subject) || body is null)
                    return BuildErrorResponse(id, -32602, "missing 'toProject', 'subject', or 'body'");
                if (string.IsNullOrEmpty(fromProject))
                    return BuildErrorResponse(id, -32603, "Sender project unknown — bridge did not provide FIREPIT_PROJECT_NAME context");
                var result = await _backend.SendInboxAsync(fromProject, toProject, subject, body, priority);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.InboxWriteResult)));
            }
            case "firepit_add_command":
            {
                var projectName = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(projectName)) projectName = ctx.ProjectName;
                if (string.IsNullOrEmpty(projectName))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");

                var cmdName = args["name"]?.GetValue<string>();
                var type    = args["type"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cmdName))
                    return BuildErrorResponse(id, -32602, "missing 'name'");
                if (string.IsNullOrEmpty(type))
                    return BuildErrorResponse(id, -32602, "missing 'type'");

                var spec = new AddCommandSpec(
                    Name:        cmdName,
                    Type:        type,
                    Icon:        args["icon"]?.GetValue<string?>(),
                    Command:     args["command"]?.GetValue<string?>(),
                    Args:        ReadStringArray(args["args"]),
                    Prompt:      args["prompt"]?.GetValue<string?>(),
                    Url:         args["url"]?.GetValue<string?>(),
                    Cwd:         args["cwd"]?.GetValue<string?>(),
                    Env:         ReadStringMap(args["env"]),
                    Elevated:    args["elevated"]?.GetValue<bool?>(),
                    Confirm:     args["confirm"]?.GetValue<bool?>(),
                    Window:      args["window"]?.GetValue<string?>(),
                    LongRunning: args["longRunning"]?.GetValue<bool?>());
                var result = await _backend.AddProjectCommandAsync(projectName, spec);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_list_commands":
            {
                var projectName = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(projectName)) projectName = ctx.ProjectName;
                if (string.IsNullOrEmpty(projectName))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.ListProjectCommandsAsync(projectName);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.CommandListResult)));
            }
            case "firepit_remove_command":
            {
                var projectName = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(projectName)) projectName = ctx.ProjectName;
                if (string.IsNullOrEmpty(projectName))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var cmdName = args["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cmdName))
                    return BuildErrorResponse(id, -32602, "missing 'name'");
                var result = await _backend.RemoveProjectCommandAsync(projectName, cmdName);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_knowledge_search":
            {
                var query = args["query"]?.GetValue<string>();
                if (string.IsNullOrEmpty(query))
                    return BuildErrorResponse(id, -32602, "missing 'query'");
                var scope = args["scope"]?.GetValue<string?>() ?? "both";
                var limit = args["limit"]?.GetValue<int?>() ?? 8;
                var projectName = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(projectName)) projectName = ctx.ProjectName;
                var result = await _backend.SearchKnowledgeAsync(
                    string.IsNullOrEmpty(projectName) ? null : projectName, scope, query, limit);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.KnowledgeSearchResult)));
            }
            case "firepit_knowledge_get":
            {
                var path = args["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path))
                    return BuildErrorResponse(id, -32602, "missing 'path'");
                var scope = args["scope"]?.GetValue<string>();
                if (string.IsNullOrEmpty(scope)) scope = ctx.ProjectName;
                if (string.IsNullOrEmpty(scope))
                    return BuildErrorResponse(id, -32602, "missing 'scope' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.GetKnowledgeDocumentAsync(scope, path);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.KnowledgeDocumentResult)));
            }
            case "firepit_knowledge_add":
            {
                var title   = args["title"]?.GetValue<string>();
                var content = args["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(title))
                    return BuildErrorResponse(id, -32602, "missing 'title'");
                if (content is null)
                    return BuildErrorResponse(id, -32602, "missing 'content'");
                var scope = args["scope"]?.GetValue<string>();
                if (string.IsNullOrEmpty(scope)) scope = ctx.ProjectName;
                if (string.IsNullOrEmpty(scope))
                    return BuildErrorResponse(id, -32602, "missing 'scope' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var pinned = args["pinned"]?.GetValue<bool?>() ?? false;
                var result = await _backend.AddKnowledgeDocumentAsync(scope, title, content, pinned);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.KnowledgeDocumentResult)));
            }
            case "firepit_knowledge_update":
            {
                var path    = args["path"]?.GetValue<string>();
                var content = args["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path))
                    return BuildErrorResponse(id, -32602, "missing 'path'");
                if (content is null)
                    return BuildErrorResponse(id, -32602, "missing 'content'");
                var scope = args["scope"]?.GetValue<string>();
                if (string.IsNullOrEmpty(scope)) scope = ctx.ProjectName;
                if (string.IsNullOrEmpty(scope))
                    return BuildErrorResponse(id, -32602, "missing 'scope' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var title  = args["title"]?.GetValue<string>();
                var pinned = args["pinned"]?.GetValue<bool?>();
                var result = await _backend.UpdateKnowledgeDocumentAsync(scope, path, content, title, pinned);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.KnowledgeDocumentResult)));
            }
            case "firepit_knowledge_delete":
            {
                var path = args["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path))
                    return BuildErrorResponse(id, -32602, "missing 'path'");
                var scope = args["scope"]?.GetValue<string>();
                if (string.IsNullOrEmpty(scope)) scope = ctx.ProjectName;
                if (string.IsNullOrEmpty(scope))
                    return BuildErrorResponse(id, -32602, "missing 'scope' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.DeleteKnowledgeDocumentAsync(scope, path);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_blueprint_list":
            {
                var result = await _backend.ListBlueprintsAsync();
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.BlueprintListResult)));
            }
            case "firepit_blueprint_check":
            {
                var project   = args["projectName"]?.GetValue<string>();
                var blueprint = args["blueprint"]?.GetValue<string?>() ?? "firepit";
                var result = await _backend.CheckBlueprintAsync(
                    string.IsNullOrEmpty(project) ? null : project, blueprint);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.BlueprintCheckResult)));
            }
            case "firepit_blueprint_apply":
            {
                var project   = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var blueprint = args["blueprint"]?.GetValue<string?>() ?? "firepit";
                var fix       = args["fixBlanketIgnores"]?.GetValue<bool?>() ?? false;
                var result = await _backend.ApplyBlueprintAsync(project, blueprint, fix);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.BlueprintApplyResult)));
            }
            case "firepit_artifact_add":
            {
                var path = args["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path))
                    return BuildErrorResponse(id, -32602, "missing 'path'");
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var label = args["label"]?.GetValue<string?>();
                var note  = args["note"]?.GetValue<string?>();
                var result = await _backend.AddArtifactAsync(project, path, label, note);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_artifact_remove":
            {
                var path  = args["path"]?.GetValue<string?>();
                var label = args["label"]?.GetValue<string?>();
                if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(label))
                    return BuildErrorResponse(id, -32602, "one of 'path' or 'label' is required");
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.RemoveArtifactAsync(project, path, label);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ToolCallResult)));
            }
            case "firepit_artifact_list":
            {
                var project = args["projectName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(project)) project = ctx.ProjectName;
                if (string.IsNullOrEmpty(project))
                    return BuildErrorResponse(id, -32602, "missing 'projectName' (and caller did not supply FIREPIT_PROJECT_NAME)");
                var result = await _backend.ListArtifactsAsync(project);
                return BuildResult(id, BuildContentJson(JsonSerializer.Serialize(result, McpJsonContext.Default.ArtifactListResult)));
            }
            default:
                return BuildErrorResponse(id, -32601, $"Unknown tool: {name}");
        }
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return null;
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is null) continue;
            list.Add(item.GetValue<string>());
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string?>? ReadStringMap(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var map = new Dictionary<string, string?>(obj.Count);
        foreach (var (key, value) in obj)
        {
            map[key] = value?.GetValue<string?>();
        }
        return map;
    }

    /// <summary>
    /// Per-connection context handshake parsed from the first frame on the
    /// pipe. Carries the calling agent's FIREPIT_PROJECT_NAME so handlers
    /// like firepit_send_to know which project a tool call originated from.
    /// </summary>
    public sealed record ConnectionContext(string ProjectName, int Pid)
    {
        public static readonly ConnectionContext Empty = new("", 0);
    }

    // --- resources/list -------------------------------------------------

    private JsonObject BuildResourcesList()
    {
        var resources = new JsonArray();
        foreach (var def in McpResourceDefinitions.All)
        {
            resources.Add(new JsonObject
            {
                ["uri"]         = def.Uri,
                ["name"]        = def.Name,
                ["description"] = def.Description,
                ["mimeType"]    = def.MimeType,
            });
        }
        return new JsonObject { ["resources"] = resources };
    }

    // --- resources/read -------------------------------------------------

    private async Task<string> HandleResourcesReadAsync(JsonNode? id, JsonNode? paramsNode)
    {
        var uri = paramsNode?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
            return BuildErrorResponse(id, -32602, "missing 'uri'");

        string text;
        switch (uri)
        {
            case "firepit://projects":
            {
                var projects = await _backend.ListProjectsAsync();
                text = JsonSerializer.Serialize(projects, McpJsonContext.Default.IReadOnlyListProjectInfo);
                break;
            }
            case "firepit://sessions":
            {
                var sessions = await _backend.ListSessionsAsync();
                text = JsonSerializer.Serialize(sessions, McpJsonContext.Default.IReadOnlyListSessionInfo);
                break;
            }
            case "firepit://settings":
                text = await _backend.GetRedactedSettingsAsync();
                break;
            case "firepit://config-schema":
                text = Firepit.Core.ProjectConfig.ProjectConfigScaffold
                    .BuildScaffold("<projectId>");
                return BuildResult(id, new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"]      = uri,
                            ["mimeType"] = "application/jsonc",
                            ["text"]     = text,
                        },
                    },
                });
            default:
                return BuildErrorResponse(id, -32602, $"unknown resource uri: {uri}");
        }

        var contents = new JsonArray
        {
            new JsonObject
            {
                ["uri"]      = uri,
                ["mimeType"] = "application/json",
                ["text"]     = text,
            },
        };
        return BuildResult(id, new JsonObject { ["contents"] = contents });
    }

    // --- envelope helpers -----------------------------------------------

    private static JsonObject BuildContentJson(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            },
        },
        ["isError"] = false,
    };

    private static string BuildResult(JsonNode? id, JsonNode result)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id?.DeepClone(),
            ["result"]  = result,
        };
        return env.ToJsonString();
    }

    private static string BuildErrorResponse(JsonNode? id, int code, string message)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"]    = code,
                ["message"] = message,
            },
        };
        return env.ToJsonString();
    }

    // --- io helpers -----------------------------------------------------

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
