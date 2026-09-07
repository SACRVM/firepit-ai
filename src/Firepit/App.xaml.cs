using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Firepit.Core.Settings;
using Firepit.Mcp;
using Firepit.Singleton;
using Serilog;

namespace Firepit;

public partial class App : Application
{
    private SingletonGuard? _guard;
    private McpHost? _mcpHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // First, before logging: ConfigureLogging opens a file under the
        // instance's own data root, and every path read after this point
        // depends on the answer.
        var instanceWarning = ApplyInstanceArgument(e.Args);

        ConfigureLogging();
        HookUnhandledExceptions();

        Log.Information(
            "Firepit starting (pid {Pid}, instance {Instance})",
            Environment.ProcessId,
            Firepit.Core.FirepitPaths.InstanceName ?? "default");

        if (instanceWarning is not null)
        {
            // Held until now on purpose: the logger writes into the instance's
            // own directory, so it cannot exist before the instance is named.
            // Logging it earlier would have gone to Serilog's silent default.
            Log.Warning("{Warning}", instanceWarning);
        }

        // Before the settings load below: a fresh named instance has none, and
        // an empty Firepit cannot be used to check anything.
        if (Firepit.Core.InstanceSeed.EnsureSettingsSeeded() is { } seedNote)
        {
            Log.Information("{Note}", seedNote);
        }

        // Load settings once at startup so font-scaling tokens are written into
        // Application.Resources BEFORE any Window XAML resolves StaticResource lookups.
        // MainWindow re-loads settings (cheap — same JSON file) for its own state;
        // we only need the font knob here.
        try
        {
            var initial = new JsonSettingsStore().Load();
            ApplyFontResources(initial.Ui?.ResolvedFontSize ?? UiSettings.DefaultFontSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not pre-load settings for font tokens — defaults stay in effect");
        }

        _guard = new SingletonGuard();

        if (!_guard.TryAcquire())
        {
            Log.Information("Existing instance detected — sending focus and exiting");
            await _guard.TrySendAsync(SingletonCommand.Focus(), TimeSpan.FromSeconds(2));
            _guard.Dispose();
            Shutdown(0);
            return;
        }

        _guard.StartListening(HandleSingletonCommand);
        base.OnStartup(e);

        // NOTE: do NOT attach to MainWindow.Loaded here. WPF defers the
        // StartupUri's window construction to a follow-up dispatcher op, so
        // Application.MainWindow is still null at this point and the
        // 'is MainWindow mw' check silently no-ops — that's how the MCP host
        // failed to start at all in v0.5.13–v0.5.15 (see issue #12). MainWindow
        // calls EnsureMcpHostStarted from its own OnLoaded instead.
    }

    /// <summary>
    /// Idempotent. Called by MainWindow once it has loaded, because App.OnStartup
    /// can't reliably reach MainWindow at the time it runs (StartupUri is
    /// processed after OnStartup returns).
    /// </summary>
    public void EnsureMcpHostStarted(IMcpBackend backend)
    {
        if (_mcpHost is not null) return;
        try
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.5.0";
            _mcpHost = new McpHost(backend, version);
            _mcpHost.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP host failed to start");
        }
    }

    private Task HandleSingletonCommand(SingletonCommand command)
    {
        Log.Information("Singleton command received: {Command} {Project}", command.Command, command.Project);
        return Dispatcher.InvokeAsync(() =>
        {
            if (MainWindow is null) return;
            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }
            MainWindow.Activate();
            MainWindow.Topmost = true;
            MainWindow.Topmost = false;

            if (command.Command == "summon"
                && !string.IsNullOrEmpty(command.Project)
                && MainWindow is MainWindow mw)
            {
                mw.SummonByName(command.Project);
            }
        }).Task;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Firepit shutting down (exit code {Code})", e.ApplicationExitCode);
        _mcpHost?.Dispose();
        _guard?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    public static void ApplyFontResources(int fontSize)
    {
        fontSize = Math.Clamp(fontSize, UiSettings.MinFontSize, UiSettings.MaxFontSize);
        var scale = fontSize / (double)UiSettings.DefaultFontSize;
        var r = Current.Resources;
        r["BaseFontSize"]              = (double)fontSize;
        r["SmallFontSize"]             = (double)Math.Max(UiSettings.MinFontSize - 1, fontSize - 1);
        r["MediumFontSize"]            = (double)(fontSize + 1);
        r["TitleFontSize"]             = (double)(fontSize + 2);
        r["CaptionPixelHeight"]        = 36.0 * scale;
        r["DialogCaptionPixelHeight"]  = 32.0 * scale;
        r["TabItemPixelHeight"]        = 36.0 * scale;
    }

    /// <summary>
    /// Read <c>--instance &lt;name&gt;</c> off the command line and name this
    /// process accordingly.
    /// </summary>
    /// <remarks>
    /// A bad name is not fatal here. The point of a named instance is to be
    /// able to run a build and look at it; refusing to start over a typo in
    /// the switch would defeat that, so it falls back to the default instance
    /// and the reason is returned for the caller to log once the logger
    /// exists. Nothing has been written yet at this point, so falling back is
    /// safe.
    /// </remarks>
    /// <returns>A warning to log, or null when the argument was fine.</returns>
    private static string? ApplyInstanceArgument(string[] args)
    {
        string? name = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--instance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                name = args[i + 1];
                break;
            }
            if (args[i].StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
            {
                name = args[i]["--instance=".Length..];
                break;
            }
        }

        try
        {
            Firepit.Core.FirepitPaths.Initialize(name);
            return null;
        }
        catch (ArgumentException ex)
        {
            Firepit.Core.FirepitPaths.Initialize(null);
            return $"Ignoring --instance and starting as the default instance: {ex.Message}";
        }
    }

    private static void ConfigureLogging()
    {
        var logsDir = Path.Combine(Firepit.Core.FirepitPaths.Local, "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logsDir, "firepit-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            if (MainWindow is MainWindow mw)
            {
                mw.ShowToast($"Unhandled error: {args.Exception.Message}", isError: true);
            }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "AppDomain unhandled exception (terminating={IsTerminating})", args.IsTerminating);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }
}
