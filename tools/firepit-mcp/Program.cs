// firepit-mcp.exe — pure stdio↔named-pipe tunnel.
//
// Claude Code launches this as an MCP stdio server. The wire on stdio is
// newline-delimited JSON-RPC; on the pipe it's length-prefixed JSON
// (4 bytes little-endian length + payload). The bridge does no parsing or
// dispatch — it just translates frames. The Firepit GUI's MCP host owns
// the protocol semantics.

using System.IO.Pipes;
using System.Text;

const string PipeName = "firepit-mcp";

try
{
    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(5_000);
    }
    catch (TimeoutException)
    {
        await EmitFatalAsync(DescribeConnectTimeout());
        return 1;
    }

    // Send the Firepit context frame BEFORE handing off the wire to the
    // tunnel. The host reads exactly one such frame per connection and uses
    // it to know which project the calling agent runs in (drives the 'from'
    // field of firepit_send_to, etc.).
    var fromProject = Environment.GetEnvironmentVariable("FIREPIT_PROJECT_NAME") ?? "";
    var pid         = Environment.ProcessId;
    // Plain concat; raw-string interpolation gets confused by the trailing
    // }}}} sequence (interpolation close + JSON close braces).
    var contextJson = "{\"_firepit_context\":{\"projectName\":\""
        + Escape(fromProject) + "\",\"pid\":" + pid + "}}";
    var contextBytes = Encoding.UTF8.GetBytes(contextJson);
    var lenPrefix = BitConverter.GetBytes(contextBytes.Length);
    await pipe.WriteAsync(lenPrefix);
    await pipe.WriteAsync(contextBytes);
    await pipe.FlushAsync();

    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();

    var stdinTask = PumpStdinToPipeAsync(stdin, pipe);
    var pipeTask  = PumpPipeToStdoutAsync(pipe, stdout);

    // Whichever side ends first (stdin EOF when Claude closes, or pipe break
    // when Firepit shuts down), exit. The Process is short-lived by design.
    await Task.WhenAny(stdinTask, pipeTask);
    return 0;
}
catch (Exception ex)
{
    await EmitFatalAsync($"firepit-mcp internal error: {ex.Message}");
    return 1;
}

// ConnectAsync throws the same TimeoutException whether the pipe does not
// exist at all or exists with every instance already taken. Guessing "GUI is
// not running" for both is what made slot exhaustion read as a random defect
// for months — so look before reporting.
static string DescribeConnectTimeout()
{
    bool pipeExists;
    try
    {
        pipeExists = Directory.EnumerateFiles(@"\\.\pipe\")
            .Any(p => string.Equals(Path.GetFileName(p), PipeName, StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        // Can't enumerate — fall back to the old assumption rather than lie
        // in the other direction.
        pipeExists = false;
    }

    return pipeExists
        ? "Firepit is running, but all of its MCP connection slots are in use. Close a Firepit tab and try again."
        : "Firepit GUI is not running. Start Firepit and try again.";
}

static async Task PumpStdinToPipeAsync(Stream stdin, Stream pipe)
{
    using var reader = new StreamReader(stdin, Encoding.UTF8, leaveOpen: true);
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0) continue;
        var bytes = Encoding.UTF8.GetBytes(line);
        var lengthBytes = BitConverter.GetBytes(bytes.Length); // little-endian on x64
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
        if (length <= 0 || length > 16 * 1024 * 1024)
        {
            // Bogus length — bail rather than allocate gigabytes.
            return;
        }
        var payload = new byte[length];
        if (!await ReadExactlyAsync(pipe, payload, 0, length)) return;
        await stdout.WriteAsync(payload);
        stdout.WriteByte((byte)'\n');
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
    // Reply with a JSON-RPC-shaped error so Claude surfaces it cleanly.
    var json = $$"""{"jsonrpc":"2.0","error":{"code":-32099,"message":"{{Escape(message)}}"},"id":null}""";
    var bytes = Encoding.UTF8.GetBytes(json + "\n");
    await Console.OpenStandardOutput().WriteAsync(bytes);
}

static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
