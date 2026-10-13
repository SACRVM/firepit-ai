using System.IO;
using Firepit.Core.Jobs;
using Firepit.Process;

namespace Firepit.Process.Tests;

/// <summary>
/// End-to-end runner tests use a script as a deterministic stand-in for the
/// real <c>claude</c> binary: a .bat file on Windows, a /bin/sh script
/// elsewhere. Each prints exactly the bytes the test wants on stdout/stderr
/// and exits with the desired code, so the runner can be verified without
/// depending on a Claude install or hitting the network.
/// </summary>
public class ProcessJobRunnerTests : IDisposable
{
    private readonly string _projectPath;

    public ProcessJobRunnerTests()
    {
        _projectPath = Path.Combine(Path.GetTempPath(), "firepit-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectPath, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFakeClaude(string batchBody, string shBody)
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(_projectPath, "fake-claude.bat");
            // ConPty's friends use Encoding.Default; .bat needs the active code page.
            File.WriteAllText(bat, "@echo off\r\n" + batchBody + "\r\n");
            return bat;
        }

        var sh = Path.Combine(_projectPath, "fake-claude.sh");
        File.WriteAllText(sh, "#!/bin/sh\n" + shBody + "\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private JobRunRequest MakeRequest(string fakeClaudePath) =>
        new(
            ProjectPath: _projectPath,
            ProjectName: "demo",
            JobName: "check-mails",
            Prompt: "/check-mails",
            Trigger: JobTrigger.Manual,
            ClaudeExecutable: fakeClaudePath,
            TimeoutSeconds: 30);

    [Fact]
    public async Task SuccessfulRun_ParsesJsonAndReportsSuccess()
    {
        var fake = WriteFakeClaude("""
            echo {"type":"result","result":"ok","total_cost_usd":0.01,"usage":{"input_tokens":10,"output_tokens":5}}
            exit /b 0
            """, """
            echo '{"type":"result","result":"ok","total_cost_usd":0.01,"usage":{"input_tokens":10,"output_tokens":5}}'
            exit 0
            """);

        var runner = new ProcessJobRunner();
        var outcome = await runner.RunAsync(MakeRequest(fake), CancellationToken.None);

        Assert.Equal(JobRunStatus.Success, outcome.Status);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("\"result\":\"ok\"", outcome.StdoutInline);
        Assert.NotNull(outcome.ClaudeMetadata);
        Assert.Equal(10, outcome.ClaudeMetadata!.TokensInput);
        Assert.Equal(5, outcome.ClaudeMetadata.TokensOutput);
        Assert.Equal(0.01m, outcome.ClaudeMetadata.CostUsd);
        Assert.Equal("ok", outcome.ClaudeMetadata.AssistantMessage);
        Assert.False(outcome.StdoutTruncated);
    }

    [Fact]
    public async Task NonZeroExit_ReportsFailureAndKeepsStderr()
    {
        var fake = WriteFakeClaude("""
            echo something went wrong 1>&2
            exit /b 2
            """, """
            echo something went wrong >&2
            exit 2
            """);

        var runner = new ProcessJobRunner();
        var outcome = await runner.RunAsync(MakeRequest(fake), CancellationToken.None);

        Assert.Equal(JobRunStatus.Failure, outcome.Status);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("something went wrong", outcome.Stderr);
        Assert.Null(outcome.ClaudeMetadata); // parser skipped on failure
    }

    [Fact]
    public async Task Timeout_KillsChildAndReportsTimeout()
    {
        // `timeout` exits immediately under redirected stdin. `ping -n 11 127.0.0.1`
        // sleeps ~10 seconds without needing a TTY.
        var fake = WriteFakeClaude("""
            ping -n 11 127.0.0.1 > nul
            echo never
            """, """
            sleep 10
            echo never
            """);

        var request = MakeRequest(fake) with { TimeoutSeconds = 1 };
        var runner = new ProcessJobRunner();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await runner.RunAsync(request, CancellationToken.None);
        sw.Stop();

        Assert.Equal(JobRunStatus.Timeout, outcome.Status);
        Assert.Null(outcome.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"runner did not enforce timeout (took {sw.Elapsed})");
    }

    [Fact]
    public async Task SpawnFailure_DoesNotThrow_ReportsFailureWithStderr()
    {
        var request = MakeRequest(fakeClaudePath: @"C:\definitely\does\not\exist\fake.exe");
        var runner = new ProcessJobRunner();

        var outcome = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(JobRunStatus.Failure, outcome.Status);
        Assert.Null(outcome.ExitCode);
        Assert.Contains("failed to spawn", outcome.Stderr);
    }

    [Fact]
    public async Task ProjectEnvVarsInjected_VisibleToChild()
    {
        var fake = WriteFakeClaude("""
            echo project=%FIREPIT_PROJECT_NAME% job=%FIREPIT_JOB_NAME% trigger=%FIREPIT_JOB_TRIGGER%
            exit /b 0
            """, """
            echo "project=$FIREPIT_PROJECT_NAME job=$FIREPIT_JOB_NAME trigger=$FIREPIT_JOB_TRIGGER"
            exit 0
            """);

        var runner = new ProcessJobRunner();
        var outcome = await runner.RunAsync(MakeRequest(fake), CancellationToken.None);

        Assert.Contains("project=demo", outcome.StdoutInline);
        Assert.Contains("job=check-mails", outcome.StdoutInline);
        Assert.Contains("trigger=manual", outcome.StdoutInline);
    }
}
