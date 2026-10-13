using Firepit.Core.ProjectConfig;
using Firepit.Core.Projects;
using Firepit.Core.Secrets;
using Firepit.Core.Settings;

namespace Firepit.Core.Mcp;

public sealed class SettingsBackedMcpRegistry : IMcpRegistry
{
    /// <summary>
    /// Built-in "firepit" server entry, seeded into every registry so users
    /// don't have to declare it in their global settings.json to use it. The
    /// command is just <c>firepit-mcp</c>; the bridge sits on PATH and tunnels
    /// stdio to the in-process named-pipe MCP host. Users can override by
    /// declaring their own <c>"firepit"</c> entry in settings.mcpServers.
    /// </summary>
    public const string BuiltInFirepitId = "firepit";

    private readonly IReadOnlyList<McpRegistryEntry> _entries;
    private readonly IReadOnlyDictionary<string, McpRegistryEntry> _byId;
    private readonly IReadOnlyDictionary<string, (IReadOnlyList<string> Active, IReadOnlyDictionary<string, McpOverride> Overrides)> _legacyProjectActivations;
    private readonly Func<string, ProjectConfig.ProjectConfig?>? _projectConfigLoader;
    private readonly ISecretResolver _resolver;
    private readonly Action<string>? _warn;

    public SettingsBackedMcpRegistry(
        FirepitSettings settings,
        ISecretResolver resolver,
        Func<string, ProjectConfig.ProjectConfig?>? projectConfigLoader = null,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _projectConfigLoader = projectConfigLoader;
        _warn = warn;

        var userEntries = (settings.McpServers ?? new Dictionary<string, McpServerSettings>())
            .Select(kvp => MapEntry(kvp.Key, kvp.Value))
            .ToArray();

        // Always seed firepit unless the user explicitly overrides it. v0.5.13
        // and earlier silently dropped any project activation whose id wasn't
        // declared in global settings — which left .firepit's own MCP tools
        // unreachable on a fresh install (issue #12).
        var hasUserFirepit = userEntries.Any(e =>
            string.Equals(e.Id, BuiltInFirepitId, StringComparison.OrdinalIgnoreCase));
        var allEntries = hasUserFirepit
            ? (IReadOnlyList<McpRegistryEntry>)userEntries
            : (IReadOnlyList<McpRegistryEntry>)new[] { BuildFirepitBuiltIn() }.Concat(userEntries).ToArray();

        _entries = allEntries;
        _byId = _entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var projectMap = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyDictionary<string, McpOverride>)>(PlatformText.PathComparer);
        foreach (var project in settings.Projects ?? [])
        {
            if (project.McpServers is null || project.McpServers.Count == 0)
            {
                continue;
            }
            var overrides = (project.McpOverrides ?? new Dictionary<string, McpOverrideSettings>())
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => MapOverride(kvp.Value),
                    StringComparer.OrdinalIgnoreCase);
            projectMap[project.Path] = (project.McpServers, overrides);
        }
        _legacyProjectActivations = projectMap;
    }

    public IReadOnlyList<McpRegistryEntry> All => _entries;

    public McpRegistryEntry? Find(string id) => _byId.TryGetValue(id, out var entry) ? entry : null;

    public IReadOnlyList<ResolvedMcpServer> ResolveForProject(ProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Per-project .firepit/config.json wins over the legacy
        // settings.Projects[] activations. After Phase 1's migration runs the
        // legacy entries should be stripped, but we keep the fallback so a
        // hand-edited settings.json continues to work during the transition.
        var projectConfig = _projectConfigLoader?.Invoke(context.Path);
        IReadOnlyList<ResolvedMcpServer> resolved;
        if (projectConfig?.McpActivations is { Count: > 0 } activations)
        {
            resolved = ResolveActivations(activations);
        }
        else if (_legacyProjectActivations.TryGetValue(context.Path, out var activation))
        {
            var list = new List<ResolvedMcpServer>(activation.Active.Count);
            foreach (var id in activation.Active)
            {
                if (!_byId.TryGetValue(id, out var entry))
                {
                    continue;
                }
                activation.Overrides.TryGetValue(id, out var overrideEntry);
                list.Add(Resolve(entry, overrideEntry));
            }
            resolved = list;
        }
        else
        {
            resolved = [];
        }

        // The Firepit built-in MCP server powers core product features
        // (toolbar Inbox button, firepit_send_to cross-project messaging,
        // tab control). Activating it per-project would mean every new
        // project needs an opt-in step before those buttons work — the
        // Inbox button in particular looked broken because the spawned
        // Claude session had no firepit_inbox_* tools. v0.5.20 makes it
        // implicit; users who explicitly list { "id": "firepit", ... } in
        // mcpActivations to pass overrides win (dedup by id below).
        if (_byId.TryGetValue(BuiltInFirepitId, out var firepitEntry)
            && !resolved.Any(r => string.Equals(r.Id, BuiltInFirepitId, StringComparison.Ordinal)))
        {
            var implicitFirepit = Resolve(firepitEntry, null);
            return [implicitFirepit, .. resolved];
        }
        return resolved;
    }

    private IReadOnlyList<ResolvedMcpServer> ResolveActivations(IReadOnlyList<ProjectMcpActivation> activations)
    {
        var resolved = new List<ResolvedMcpServer>(activations.Count);
        foreach (var act in activations)
        {
            if (!_byId.TryGetValue(act.Id, out var entry))
            {
                // Pre-v0.5.16 this was a silent drop — projects activating an
                // MCP id that wasn't declared in global settings would get 0
                // servers projected with no clue why (issue #12).
                _warn?.Invoke(
                    $"Project activates MCP id '{act.Id}' but no such server is registered. " +
                    "Declare it in %APPDATA%\\Firepit\\settings.json under mcpServers, or remove the activation.");
                continue;
            }
            var ov = new McpOverride(
                Args:        act.ArgOverrides,
                Environment: act.EnvOverrides,
                Headers:     act.HeaderOverrides);
            resolved.Add(Resolve(entry, ov));
        }
        return resolved;
    }

    private ResolvedMcpServer Resolve(McpRegistryEntry entry, McpOverride? overrideEntry)
    {
        var warnings = new List<string>();
        var args = (overrideEntry?.Args ?? entry.Args ?? [])
            .Select(arg => InterpolateInto(arg, warnings, $"args of {entry.Id}"))
            .ToArray();
        var env = MergeMaps(entry.Environment, overrideEntry?.Environment, warnings, $"env of {entry.Id}", PlatformText.EnvironmentNames);
        var headers = MergeMaps(entry.Headers, overrideEntry?.Headers, warnings, $"headers of {entry.Id}", StringComparer.OrdinalIgnoreCase);
        var url = entry.Url is null ? null : InterpolateInto(entry.Url, warnings, $"url of {entry.Id}");

        return new ResolvedMcpServer(
            Id: entry.Id,
            DisplayName: entry.DisplayName,
            Transport: entry.Transport,
            Command: entry.Command,
            Args: args,
            Environment: env,
            Url: url,
            Headers: headers,
            ResolutionWarnings: warnings);
    }

    private string InterpolateInto(string value, List<string> warnings, string context)
    {
        var result = SecretInterpolation.Interpolate(value, _resolver);
        foreach (var missing in result.MissingTokens)
        {
            warnings.Add($"unresolved {missing} in {context}");
        }
        return result.ResolvedValue;
    }

    private IReadOnlyDictionary<string, string> MergeMaps(
        IReadOnlyDictionary<string, string?>? baseline,
        IReadOnlyDictionary<string, string?>? overrides,
        List<string> warnings,
        string context,
        StringComparer keys)
    {
        var merged = new Dictionary<string, string>(keys);
        if (baseline is not null)
        {
            foreach (var (key, value) in baseline)
            {
                if (value is null) continue;
                merged[key] = InterpolateInto(value, warnings, context);
            }
        }
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null)
                {
                    merged.Remove(key);
                }
                else
                {
                    merged[key] = InterpolateInto(value, warnings, context);
                }
            }
        }
        return merged;
    }

    private static McpRegistryEntry MapEntry(string id, McpServerSettings source) => new(
        Id: id,
        DisplayName: source.DisplayName,
        Transport: ParseTransport(source.Transport),
        Description: source.Description,
        Command: source.Command,
        Args: source.Args,
        Environment: source.Environment,
        Url: source.Url,
        Headers: source.Headers);

    private static McpOverride MapOverride(McpOverrideSettings source) => new(
        Args: source.Args,
        Environment: source.Environment,
        Headers: source.Headers);

    private static McpTransport ParseTransport(string value) => value?.ToLowerInvariant() switch
    {
        "stdio" => McpTransport.Stdio,
        "http"  => McpTransport.Http,
        "sse"   => McpTransport.Sse,
        _       => McpTransport.Stdio,
    };

    private static McpRegistryEntry BuildFirepitBuiltIn() => new(
        Id:          BuiltInFirepitId,
        DisplayName: "Firepit",
        Transport:   McpTransport.Stdio,
        Description: "Firepit built-in: cross-project messaging (firepit_send_to / firepit_inbox_*), " +
                     "project introspection, tab control. Bridge: firepit-mcp.exe ⇄ named-pipe ⇄ Firepit GUI.",
        Command:     "firepit-mcp",
        Args:        null,
        Environment: null,
        Url:         null,
        Headers:     null);
}
