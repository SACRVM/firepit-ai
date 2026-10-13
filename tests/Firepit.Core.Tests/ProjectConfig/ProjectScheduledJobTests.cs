using System.IO;
using System.Text.Json;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Settings;

namespace Firepit.Core.Tests.ProjectConfig;

public class ProjectScheduledJobTests : IDisposable
{
    private readonly string _projectPath;

    public ProjectScheduledJobTests()
    {
        _projectPath = Path.Combine(Path.GetTempPath(), "firepit-jobs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectPath, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Roundtrip_PreservesAllJobFields()
    {
        var config = new Firepit.Core.ProjectConfig.ProjectConfig(
            Id: "demo",
            ScheduledJobs: new[]
            {
                new ProjectScheduledJob(
                    Name: "check-mails",
                    Prompt: "/check-mails",
                    Schedule: "*/30 * * * *",
                    Enabled: true,
                    TimeoutSeconds: 600,
                    AllowedTools: new[] { "Read", "Bash" },
                    MaxTurns: 5,
                    MaxBudgetUsd: 0.25m,
                    SkipPermissions: true,
                    Timezone: "Europe/Berlin",
                    OnConcurrent: JobConcurrencyPolicy.Queue,
                    Notify: JobNotifyPolicy.OnChange),
            });

        var store = new JsonProjectConfigStore();
        store.Save(_projectPath, config);
        var reloaded = store.Load(_projectPath);

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.ScheduledJobs);
        var job = Assert.Single(reloaded.ScheduledJobs!);

        Assert.Equal("check-mails", job.Name);
        Assert.Equal("/check-mails", job.Prompt);
        Assert.Equal("*/30 * * * *", job.Schedule);
        Assert.True(job.Enabled);
        Assert.Equal(600, job.TimeoutSeconds);
        Assert.Equal(new[] { "Read", "Bash" }, job.AllowedTools);
        Assert.Equal(5, job.MaxTurns);
        Assert.Equal(0.25m, job.MaxBudgetUsd);
        Assert.True(job.SkipPermissions);
        Assert.Equal("Europe/Berlin", job.Timezone);
        Assert.Equal(JobConcurrencyPolicy.Queue, job.OnConcurrent);
        Assert.Equal(JobNotifyPolicy.OnChange, job.Notify);
    }

    [Fact]
    public void Minimum_RequiredFieldsOnly_RoundtripsCleanly()
    {
        var config = new Firepit.Core.ProjectConfig.ProjectConfig(
            ScheduledJobs: new[]
            {
                new ProjectScheduledJob(
                    Name: "minimal",
                    Prompt: "do the thing",
                    Schedule: "0 9 * * 1-5"),
            });

        var store = new JsonProjectConfigStore();
        store.Save(_projectPath, config);
        var reloaded = store.Load(_projectPath);

        var job = Assert.Single(reloaded!.ScheduledJobs!);
        Assert.Equal("minimal", job.Name);
        Assert.Equal("do the thing", job.Prompt);
        Assert.Equal("0 9 * * 1-5", job.Schedule);
        Assert.Null(job.Enabled);
        Assert.Null(job.TimeoutSeconds);
        Assert.Null(job.OnConcurrent);
        Assert.Null(job.Notify);
    }

    [Fact]
    public void EnumsSerializeAsCamelCaseStrings()
    {
        var config = new Firepit.Core.ProjectConfig.ProjectConfig(
            ScheduledJobs: new[]
            {
                new ProjectScheduledJob("j", "p", "* * * * *",
                    OnConcurrent: JobConcurrencyPolicy.KillAndRestart,
                    Notify: JobNotifyPolicy.Always),
            });

        var store = new JsonProjectConfigStore();
        store.Save(_projectPath, config);

        var json = File.ReadAllText(JsonProjectConfigStore.ResolvePath(_projectPath));
        Assert.Contains("\"killAndRestart\"", json);
        Assert.Contains("\"always\"", json);
    }

    [Fact]
    public void RunsConfig_RoundtripsCleanly()
    {
        var config = new Firepit.Core.ProjectConfig.ProjectConfig(
            Runs: new ProjectRunsConfig(
                BadgePolicy: RunBadgePolicy.FailuresOnly,
                RetentionDays: 7));

        var store = new JsonProjectConfigStore();
        store.Save(_projectPath, config);
        var reloaded = store.Load(_projectPath);

        Assert.NotNull(reloaded!.Runs);
        Assert.Equal(RunBadgePolicy.FailuresOnly, reloaded.Runs!.BadgePolicy);
        Assert.Equal(7, reloaded.Runs.RetentionDays);
    }

    [Fact]
    public void Scaffold_DocumentsScheduledJobsSection()
    {
        var content = ProjectConfigScaffold.BuildScaffold("demo");
        Assert.Contains("scheduledJobs", content);
        Assert.Contains("check-mails", content);
        Assert.Contains("weekly-review", content);
    }

    [Fact]
    public void Scaffold_DocumentsRunsSection()
    {
        var content = ProjectConfigScaffold.BuildScaffold("demo");
        Assert.Contains("\"runs\"", content);
        Assert.Contains("retentionDays", content);
    }
}
