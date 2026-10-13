using System.Runtime.InteropServices;
using Firepit.Core;
using Firepit.Mcp;
using Firepit.Workspace;
using Serilog;
using Serilog.Events;

// firepitd — Firepit as a user service: the workspace (projects, knowledge,
// inbox, artifacts, commands, blueprints, jobs) and the MCP host that Claude
// Code sessions reach through firepit-mcp.
//
//   firepitd [--instance <name>] [--no-jobs]
//
// One per instance: a second start finds the lock taken and exits.

var instance = ArgValue(args, "--instance") ?? Environment.GetEnvironmentVariable(FirepitPaths.InstanceEnvironmentVariable);
var runJobs = !args.Contains("--no-jobs");

try
{
    FirepitPaths.Initialize(string.IsNullOrWhiteSpace(instance) ? null : instance);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"firepitd: {ex.Message}");
    return 2;
}

ConfigureLogging();
var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

try
{
    using var instanceLock = TryLock();
    if (instanceLock is null)
    {
        Log.Error("Another firepitd already runs for this instance ({Runtime}); exiting", FirepitPaths.Runtime);
        return 1;
    }

    Log.Information("firepitd {Version} starting{Suffix}; config {Config}, data {Data}",
        version, FirepitPaths.DisplaySuffix, FirepitPaths.Roaming, FirepitPaths.Local);

    var stopping = new CancellationTokenSource();
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

    await using var workspace = new FirepitWorkspace(new HeadlessTabHost(), runJobs: runJobs);
    await workspace.StartAsync(stopping.Token);
    Log.Information("Workspace ready: {Count} project(s) under {Root}",
        workspace.Projects.Count, workspace.Settings.ProjectsRoot);

    using var host = new McpHost(workspace, version);
    host.Start();

    // A daemon has no project picker to trigger a rescan, so it looks every
    // half minute for repos cloned into the root or settings edited by hand.
    while (!stopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stopping.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        try
        {
            if (await workspace.RescanIfChangedAsync())
            {
                Log.Information("Projects rescanned: {Count} project(s)", workspace.Projects.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Project rescan failed");
        }
    }

    Log.Information("firepitd stopping");
    return 0;

    void Stop(PosixSignalContext context)
    {
        context.Cancel = true;
        stopping.Cancel();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "firepitd failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void ConfigureLogging()
{
    var logsDir = Path.Combine(FirepitPaths.Local, "logs");
    Directory.CreateDirectory(logsDir);
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .WriteTo.File(
            Path.Combine(logsDir, "firepitd-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true)
        // systemd adds its own timestamp to every journal line.
        .WriteTo.Console(
            outputTemplate: "{Level:u3} {Message:lj}{NewLine}{Exception}",
            standardErrorFromLevel: LogEventLevel.Verbose)
        .CreateLogger();
}

// An exclusive lock on a file in the runtime directory. .NET takes flock(2)
// for FileShare.None off Windows, and the kernel drops it when the process
// dies — a crash leaves no lock behind, unlike a pid file.
static FileStream? TryLock()
{
    var dir = FirepitPaths.Runtime;
    if (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(dir);
    }
    else
    {
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    try
    {
        return new FileStream(Path.Combine(dir, "firepitd.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        return null;
    }
}
