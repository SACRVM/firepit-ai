using System.Collections.Concurrent;
using Firepit.Core.Jobs;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Time;

namespace Firepit.Core.Tests.Jobs;

public class JobSchedulerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.FindSystemTimeZoneById("UTC");

    private sealed class FakeClock : IActivityClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 5, 13, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeRunner : IJobRunner
    {
        public readonly ConcurrentBag<(string Job, JobTrigger Trigger, DateTimeOffset StartedAt)> Invocations = new();
        public TaskCompletionSource? Gate;
#pragma warning disable CS0649 // assigned via object initializer in tests
        public Func<JobRunRequest, JobRunOutcome>? OutcomeFactory;
#pragma warning restore CS0649

        public async Task<JobRunOutcome> RunAsync(JobRunRequest request, CancellationToken ct)
        {
            var startedAt = DateTimeOffset.UtcNow;
            Invocations.Add((request.JobName, request.Trigger, startedAt));
            if (Gate is not null) await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            var outcome = OutcomeFactory?.Invoke(request) ?? new JobRunOutcome(
                JobRunStatus.Success, 0, startedAt, DateTimeOffset.UtcNow,
                "fake", "", false, null, "");
            return outcome;
        }
    }

    private sealed class FakeHistory : IJobHistoryStore
    {
        public readonly ConcurrentBag<(string Job, string Prompt, JobTrigger Trigger, JobRunStatus Status)> Records = new();
        public readonly ConcurrentDictionary<string, DateTimeOffset> LastRun = new();
        public int RecoverCalls;

        public Task RecordAsync(string projectPath, string projectName, string jobName,
            string prompt, JobTrigger trigger, JobRunOutcome outcome, CancellationToken ct)
        {
            Records.Add((jobName, prompt, trigger, outcome.Status));
            LastRun[$"{projectPath}||{jobName}"] = outcome.StartedAt;
            return Task.CompletedTask;
        }

        public DateTimeOffset? GetLastRunStartedAt(string projectPath, string jobName) =>
            LastRun.TryGetValue($"{projectPath}||{jobName}", out var v) ? v : null;

        public Task RecoverInterruptedAsync(string projectPath, CancellationToken ct)
        {
            Interlocked.Increment(ref RecoverCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSource : IJobScheduleSource
    {
        public List<JobScheduleEntry> Entries { get; } = new();
        public IReadOnlyList<JobScheduleEntry> Enumerate() => Entries;
    }

    // The scheduler fires runs on background tasks the tick doesn't await, so
    // "the runner was invoked" is only observable eventually. Poll instead of
    // sleeping a fixed amount — slow CI runners blow well past any constant.
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static JobScheduleEntry Entry(string jobName, string cron,
        JobConcurrencyPolicy? policy = null) =>
        new(
            ProjectPath: @"C:\projects\demo",
            ProjectName: "demo",
            Job: new ProjectScheduledJob(
                Name: jobName,
                Prompt: $"/{jobName}",
                Schedule: cron,
                OnConcurrent: policy),
            Timezone: Utc);

    [Fact]
    public async Task DueJob_FiresOnTick()
    {
        // Scheduler starts at 09:00; cron slot at 09:30 passes; tick at 09:30:30.
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("check-mails", "*/30 * * * *") } };

        await using var sched = new JobScheduler(source, runner, history, clock,
            JobSchedulerOptions.Defaults with { TickInterval = TimeSpan.FromMinutes(1) });

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 9, 30, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);

        Assert.Single(runner.Invocations);
        var inv = runner.Invocations.First();
        Assert.Equal("check-mails", inv.Job);
        Assert.Equal(JobTrigger.Scheduled, inv.Trigger);
    }

    [Fact]
    public async Task NotDueYet_DoesNotFire()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 5, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("check-mails", "*/30 * * * *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);
        await sched.TickOnceAsync(CancellationToken.None);
        await Task.Delay(30);

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SecondTickAtSameSlot_DoesNotRefire()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("check-mails", "*/30 * * * *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);
        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 9, 30, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);
        await sched.TickOnceAsync(CancellationToken.None);
        await Task.Delay(30); // a wrong refire needs a moment to become visible

        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task ConcurrencySkip_RecordsSkippedWhenStillRunning()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        var history = new FakeHistory();
        var source = new StaticSource
        {
            Entries = { Entry("slow", "*/30 * * * *", JobConcurrencyPolicy.Skip) },
        };

        await using var sched = new JobScheduler(source, runner, history, clock);

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 9, 30, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty); // runner is parked on Gate

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 10, 0, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);

        runner.Gate!.SetResult();
        await WaitUntilAsync(() => history.Records.Any(r => r.Status == JobRunStatus.Skipped));

        Assert.Contains(history.Records, r => r.Status == JobRunStatus.Skipped);
        Assert.Single(runner.Invocations); // only the first actually ran
    }

    [Fact]
    public async Task ConcurrencyQueue_DefersRunInsteadOfSkipping()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        var history = new FakeHistory();
        var source = new StaticSource
        {
            Entries = { Entry("slow", "*/30 * * * *", JobConcurrencyPolicy.Queue) },
        };

        await using var sched = new JobScheduler(source, runner, history, clock);

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 9, 30, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 10, 0, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);

        // No skip yet — it should be queued.
        Assert.DoesNotContain(history.Records, r => r.Status == JobRunStatus.Skipped);

        // Let the first run complete; second should fire automatically.
        runner.Gate!.SetResult();
        await WaitUntilAsync(() => runner.Invocations.Count >= 2);

        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task ManualTrigger_FiresImmediately()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 5, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("check-mails", "0 0 1 1 *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);
        await sched.TriggerNowAsync(@"C:\projects\demo", "check-mails", CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);

        Assert.Single(runner.Invocations);
        Assert.Equal(JobTrigger.Manual, runner.Invocations.First().Trigger);
    }

    [Fact]
    public async Task Catchup_FiresOnceWhenLastRunIsTooOld()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 10, 5, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();

        // Last run at 09:00 — schedule fires every 30 min, so 09:30 and 10:00 were missed.
        history.LastRun[@"C:\projects\demo||check-mails"] =
            new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero);

        var source = new StaticSource { Entries = { Entry("check-mails", "*/30 * * * *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);
        await sched.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.Invocations.Any(i => i.Trigger == JobTrigger.Catchup));
        await Task.Delay(50); // a wrong second catch-up needs a moment to become visible

        var catchups = runner.Invocations.Count(i => i.Trigger == JobTrigger.Catchup);
        Assert.Equal(1, catchups); // exactly one catch-up, not "all missed slots"
    }

    [Fact]
    public async Task ParallelManualTriggers_OnlyOneRuns()
    {
        // Two TriggerNowAsync calls fired concurrently must not produce two
        // overlapping runs — the second sees RunningTask alive and yields to
        // concurrency policy (default Skip).
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner { Gate = new TaskCompletionSource() };
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("slow", "0 0 1 1 *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);

        var t1 = sched.TriggerNowAsync(@"C:\projects\demo", "slow", CancellationToken.None);
        var t2 = sched.TriggerNowAsync(@"C:\projects\demo", "slow", CancellationToken.None);
        await Task.WhenAll(t1, t2);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);
        await Task.Delay(50); // a wrong second run needs a moment to become visible

        Assert.Single(runner.Invocations);
        runner.Gate!.SetResult();
        await Task.Delay(50);
    }

    [Fact]
    public async Task InvalidateProject_ClearsJobStateAndAllowsImmediateRefire()
    {
        // First tick at 09:30:30 fires; InvalidateProject drops LastFiredUtc;
        // a second tick at the same wall-clock time refires because the anchor
        // is back to the scheduler's startup instant.
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero) };
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource { Entries = { Entry("check-mails", "*/30 * * * *") } };

        await using var sched = new JobScheduler(source, runner, history, clock);

        clock.UtcNow = new DateTimeOffset(2026, 5, 13, 9, 30, 30, TimeSpan.Zero);
        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => !runner.Invocations.IsEmpty);
        Assert.Single(runner.Invocations);

        sched.InvalidateProject(@"C:\projects\demo");

        await sched.TickOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.Invocations.Count >= 2);
        // Without invalidation the second tick at the same slot wouldn't refire
        // (cf. SecondTickAtSameSlot_DoesNotRefire). Invalidation restores the
        // anchor so the slot is "due" again from the scheduler's POV.
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task Start_InvokesInterruptedRecoveryOncePerProject()
    {
        var clock = new FakeClock();
        var runner = new FakeRunner();
        var history = new FakeHistory();
        var source = new StaticSource
        {
            Entries =
            {
                Entry("a", "0 0 1 1 *"),
                Entry("b", "0 0 1 1 *"), // same project as 'a' — should recover once
            },
        };

        await using var sched = new JobScheduler(source, runner, history, clock);
        await sched.StartAsync(CancellationToken.None);

        Assert.Equal(1, history.RecoverCalls);
    }
}
