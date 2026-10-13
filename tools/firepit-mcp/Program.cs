// firepit-mcp — pure stdio↔Firepit tunnel.
//
// Claude Code launches this as an MCP stdio server. The wire on stdio is
// newline-delimited JSON-RPC; towards Firepit it's length-prefixed JSON
// (4 bytes little-endian length + payload) over a Unix socket on Linux and
// macOS, a named pipe on Windows. The bridge does no parsing or dispatch — it
// just translates frames. Firepit's MCP host owns the protocol semantics.

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

// Which Firepit to dial. A named instance exports FIREPIT_INSTANCE into the
// sessions it starts, so their bridges reach that instance and never the one
// running beside it. The naming rules are duplicated from Firepit.Core rather
// than referenced: this is a standalone tunnel by design and must not drag
// the app's assemblies along to ship.
var instance = Environment.GetEnvironmentVariable("FIREPIT_INSTANCE");
instance = string.IsNullOrWhiteSpace(instance) ? null : instance.Trim();

try
{
    await using var link = OperatingSystem.IsWindows()
        ? await ConnectPipeAsync(instance)
        : await ConnectSocketAsync(instance);
    if (link is null)
    {
        return 1;
    }

    // Send the Firepit context frame BEFORE handing off the wire to the
    // tunnel. The host reads exactly one such frame per connection and uses
    // it to know which project the calling agent runs in (drives the 'from'
    // field of firepit_send_to, the default projectName, etc.). Clients that
    // export no FIREPIT_PROJECT_NAME — Claude Code desktop — are placed by
    // the folder they started this bridge in.
    var fromProject = Environment.GetEnvironmentVariable("FIREPIT_PROJECT_NAME") ?? "";
    var pid         = Environment.ProcessId;
    var cwd         = Environment.CurrentDirectory;
    // Plain concat; raw-string interpolation gets confused by the trailing
    // }}}} sequence (interpolation close + JSON close braces).
    var contextJson = "{\"_firepit_context\":{\"projectName\":\""
        + Escape(fromProject) + "\",\"pid\":" + pid + ",\"cwd\":\"" + Escape(cwd) + "\"}}";
    var contextBytes = Encoding.UTF8.GetBytes(contextJson);
    var lenPrefix = BitConverter.GetBytes(contextBytes.Length);
    await link.WriteAsync(lenPrefix);
    await link.WriteAsync(contextBytes);
    await link.FlushAsync();

    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();

    var stdinTask = PumpStdinToPipeAsync(stdin, link);
    var pipeTask  = PumpPipeToStdoutAsync(link, stdout);

    // Whichever side ends first (stdin EOF when Claude closes, or the link
    // breaking when Firepit shuts down), exit. The process is short-lived by
    // design.
    await Task.WhenAny(stdinTask, pipeTask);
    return 0;
}
catch (Exception ex)
{
    await EmitFatalAsync($"firepit-mcp internal error: {ex.Message}");
    return 1;
}

// Unix: the socket in Firepit's runtime directory. A daemon that is not
// running yet is started once through systemd and given a moment to bind —
// the first session after login usually starts before anything asked for
// Firepit.
static async Task<Stream?> ConnectSocketAsync(string? instance)
{
    var path = SocketPath(instance);
    var startedDaemon = false;
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

    while (true)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException)
        {
            socket.Dispose();
        }

        if (!startedDaemon)
        {
            startedDaemon = true;
            TryStartDaemon(instance);
        }

        if (DateTime.UtcNow > deadline)
        {
            await EmitFatalAsync(
                $"Firepit is not running (nothing listens on {path}). " +
                $"Start it with: systemctl --user start {UnitName(instance)}");
            return null;
        }

        await Task.Delay(250);
    }
}

// Same rule as FirepitPaths.Runtime + McpHost.SocketPath.
static string SocketPath(string? instance)
{
    var folder = instance is null ? "firepit" : $"firepit-{instance}".ToLowerInvariant();
    var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    var parent = !string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime)
        ? runtime
        : Path.Combine(Path.GetTempPath(), $"firepit-{Environment.UserName}");
    return Path.Combine(parent, folder, "mcp.sock");
}

static string UnitName(string? instance) =>
    instance is null ? "firepit.service" : $"firepit@{instance}.service";

static void TryStartDaemon(string? instance)
{
    try
    {
        var psi = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add("--no-block");
        psi.ArgumentList.Add(UnitName(instance));
        using var process = Process.Start(psi);
        process?.WaitForExit(5_000);
    }
    catch (Exception)
    {
        // No systemd, or no unit installed: the connect loop runs out and
        // says how to start Firepit.
    }
}

// Windows: the named pipe the WPF Firepit serves (windows-legacy).
static async Task<Stream?> ConnectPipeAsync(string? instance)
{
    var pipeName = instance is null ? "firepit-mcp" : $"firepit-mcp-{instance}";
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(5_000);
        return pipe;
    }
    catch (TimeoutException)
    {
        await pipe.DisposeAsync();
        await EmitFatalAsync("Firepit is not running, or all of its MCP connection slots are in use.");
        return null;
    }
}

static async Task PumpStdinToPipeAsync(Stream stdin, Stream pipe)
{
    using var reader = new StreamReader(stdin, Encoding.UTF8, leaveOpen: true);
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0) continue;
        var bytes = Encoding.UTF8.GetBytes(line);
        var lengthBytes = BitConverter.GetBytes(bytes.Length); // little-endian on x64 and arm64
        await pipe.WriteAsync(lengthBytes);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }
}

static async Task PumpPipeToStdoutAsync(Stream pipe, Stream stdout)
{
    var lengthBuf = new byte[4];
    while (true)
    {
        if (!await ReadExactlyAsync(pipe, lengthBuf, 0, 4)) return;
        var length = BitConverter.ToInt32(lengthBuf, 0);
        if (length <= 0 || length > 16 * 1024 * 1024) return;

        var payload = new byte[length];
        if (!await ReadExactlyAsync(pipe, payload, 0, length)) return;

        await stdout.WriteAsync(payload);
        await stdout.WriteAsync("\n"u8.ToArray());
        await stdout.FlushAsync();
    }
}

static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count)
{
    var read = 0;
    while (read < count)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read));
        if (n == 0) return false;
        read += n;
    }
    return true;
}

static async Task EmitFatalAsync(string message)
{
    // Reply with a JSON-RPC-shaped error so Claude surfaces it cleanly; the
    // same line on stderr lands in Claude Code's MCP log.
    var json = $$"""{"jsonrpc":"2.0","error":{"code":-32099,"message":"{{Escape(message)}}"},"id":null}""";
    var bytes = Encoding.UTF8.GetBytes(json + "\n");
    await Console.OpenStandardOutput().WriteAsync(bytes);
    await Console.Error.WriteLineAsync($"[firepit-mcp] {message}");
}

static string Escape(string s) =>
    s.Replace("\\", "\\\\").Replace("\"", "\\\"");
