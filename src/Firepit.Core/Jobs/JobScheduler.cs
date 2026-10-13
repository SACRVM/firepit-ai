using System.Collections.Concurrent;
using Firepit.Core.ProjectConfig;
using Firepit.Core.Time;

namespace Firepit.Core.Jobs;

/// <summary>
/// In-process job scheduler. Discovers scheduled jobs via an
/// <see cref="IJobScheduleSource"/>, evaluates cron expressions on every
/// tick, dispatches due runs through <see cref="IJobRunner"/>, and
/// persists outcomes via <see cref="IJobHistoryStore"/>.
///
/// Lifetime model: ticks only while Firepit is running. On <see cref="StartAsync"/>
/// each job catches up <b>one</b> missed occurrence at most — multi-day
/// backfill is deliberately out of scope (see <c>SPEC.md</c>'s
/// local-first stance).
///
/// Concurrency: each job has at most one active run. Subsequent due
/// occurrences while a run is active are handled per
/// <see cref="JobConcurrencyPolicy"/>:
/// <list type="bullet">
///   <item><c>Skip</c> (default) — write a <c>Skipped</c> record once per missed slot</item>
///   <item><c>Queue</c> — defer up to <see cref="JobSchedulerOptions.MaxQueueDepth"/> runs</item>
///   <item><c>KillAndRestart</c> — cancel the active run, then fire a fresh one</item>
/// </list>
/// </summary>
public sealed class JobScheduler : IAsyncDisposable
{
    private readonly IJobScheduleSource _source;
    private readonly IJobRunner _runner;
    private readonly IJobHistoryStore _history;
    private readonly IActivityClock _clock;
    private readonly JobSchedulerOptions _options;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, JobState> _states = new();
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly DateTimeOffset _startedAtUtc;
    private Task? _tickLoop;
    private bool _started;

    public JobScheduler(
        IJobScheduleSource source,
        IJobRunner runner,
        IJobHistoryStore history,
        IActivityClock clock,
        JobSchedulerOptions? options = null,
        Action<string>? log = null)
    {
        _source  = source;
        _runner  = runner;
        _history = history;
        _clock   = clock;
        _options = options ?? JobSchedulerOptions.Defaults;
        _log     = log;
        _startedAtUtc = clock.UtcNow;
    }

    private int _runsObserved;

    /// <summary>Total runs fired (including skipped) since startup. Test hook.</summary>
    public int RunsObserved => Volatile.Read(ref _runsObserved);

    /// <summary>Public so the UI / MCP layer can trigger a single tick on demand.</summary>
    public Task TickOnceAsync(CancellationToken ct) => RunOneTickAsync(ct);

    /// <summary>
    /// Drop in-memory state for every job under <paramref name="projectPath"/>.
    /// Called by the host's config-watcher when a project's
    /// <c>.firepit/config.json</c> changes — renamed or deleted jobs would
    /// otherwise leave stale <c>LastFiredUtc</c> entries in our dict, which
    /// is wasted memory and (more importantly) means a removed-then-re-added
    /// job would skip its first fire.
    /// </summary>
    public void InvalidateProject(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        var prefix = PlatformText.PathKey(projectPath) + "||";
        var stale  = _states.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in stale)
        {
            _states.TryRemove(key, out _);
        }
        if (stale.Count > 0)
        {
            _log?.Invoke($"InvalidateProject {projectPath}: cleared {stale.Count} job state(s)");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) throw new InvalidOperationException("scheduler already started");
        _started = true;

        await RecoverInterruptedRunsAsync(ct).ConfigureAwait(false);
        await ApplyCatchupAsync(ct).ConfigureAwait(false);

        _tickLoop = Task.Run(() => TickLoopAsync(_lifecycle.Token), CancellationToken.None);
    }

    /// <summary>
    /// Fire a job immediately (manual trigger or MCP <c>firepit_run_now</c>).
    /// Bypasses cron, still honours concurrency policy.
    /// </summary>
    public Task TriggerNowAsync(string projectPath, string jobName, CancellationToken ct)
    {
        var entry = _source.Enumerate()
            .FirstOrDefault(e => string.Equals(e.ProjectPath, projectPath, PlatformText.Paths) &&
                                 string.Equals(e.Job.Name, jobName, StringComparison.Ordinal));
        if (entry is null) throw new InvalidOperationException($"unknown job '{jobName}' in {projectPath}");
        return DispatchAsync(entry, JobTrigger.Manual, ct);
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOneTickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log?.Invoke($"scheduler tick error: {ex.Message}");
                }
                try { await Task.Delay(_options.TickInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private async Task RunOneTickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var dispatches = new List<Task>();

        foreach (var entry in _source.Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            if (!CronEvaluator.TryParse(entry.Job.Schedule, out var schedule) || schedule is null) continue;

            var key = KeyOf(entry);
            var state = _states.GetOrAdd(key, _ => new JobState());

            // Anchor: scheduler-startup time on first sight, then the last
            // slot we actually fired. That keeps cron math from spamming
            // historic slots a brand-new tab has never seen, but does fire
            // every slot that has passed since the last fire.
            var anchor = state.LastFiredUtc ?? _startedAtUtc;
            var due    = CronEvaluator.NextOccurrence(schedule, anchor, entry.Timezone);
            if (due is null || due > now) continue;

            // Serialize the check-and-decide section per job. Without this,
            // TickOnce + the background tick loop + TriggerNowAsync can race
            // on RunningTask / QueuedTrigger and either double-fire or lose
            // updates. The actual run still runs outside the gate.
            var shouldFire = false;
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state.RunningTask is { IsCompleted: false })
                {
                    var policy = entry.Job.OnConcurrent ?? JobConcurrencyPolicy.Skip;
                    await HandleConcurrencyAsync(entry, state, due.Value, policy, ct).ConfigureAwait(false);
                    continue;
                }
                state.LastFiredUtc = due.Value;
                shouldFire = true;
            }
            finally { state.Gate.Release(); }

            if (shouldFire) dispatches.Add(DispatchAsync(entry, JobTrigger.Scheduled, ct));
        }

        // Don't block the tick on the actual runs — they may take minutes.
        // The list is just to surface fire-and-forget exceptions early.
        _ = Task.WhenAll(dispatches).ContinueWith(t =>
        {
            if (t.Exception is not null) _log?.Invoke($"dispatch failures: {t.Exception.Message}");
        }, TaskScheduler.Default);
    }

    private async Task DispatchAsync(JobScheduleEntry entry, JobTrigger trigger, CancellationToken outerCt)
    {
        var state = _states.GetOrAdd(KeyOf(entry), _ => new JobState());

        // Same gate as the tick: serialize concurrency-policy decisions and
        // RunningTask assignment. The actual run executes outside the gate.
        CancellationTokenSource cts;
        await state.Gate.WaitAsync(outerCt).ConfigureAwait(false);
        try
        {
            if (state.RunningTask is { IsCompleted: false } && trigger != JobTrigger.Scheduled)
            {
                await HandleConcurrencyAsync(entry, state, _clock.UtcNow,
                    entry.Job.OnConcurrent ?? JobConcurrencyPolicy.Skip, outerCt).ConfigureAwait(false);
                return;
            }

            state.LastFiredUtc ??= _clock.UtcNow;
            cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            state.RunningCts = cts;
        }
        finally { state.Gate.Release(); }

        var task = Task.Run(async () =>
        {
            Interlocked.Increment(ref _runsObserved);
            var request = BuildRequest(entry, trigger);
            try
            {
                var outcome = await _runner.RunAsync(request, cts.Token).ConfigureAwait(false);
                await _history.RecordAsync(entry.ProjectPath, entry.ProjectName, entry.Job.Name,
                    entry.Job.Prompt, trigger, outcome, CancellationToken.None).ConfigureAwait(false);

                // Drain the queue under the gate. Cleared first so a fresh
                // dispatch doesn't see RunningTask still-active from this run.
                JobTrigger? toDispatch = null;
                await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (state.QueuedTrigger is JobTrigger queued)
                    {
                        state.QueuedTrigger = null;
                        state.QueueDepth = Math.Max(0, state.QueueDepth - 1);
                        toDispatch = queued;
                    }
                    // Mark complete so the next tick's RunningTask check fails.
                    state.RunningTask = null;
                    state.RunningCts = null;
                }
                finally { state.Gate.Release(); }

                if (toDispatch is JobTrigger t)
                {
                    _ = Task.Run(() => DispatchAsync(entry, t, outerCt), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"job '{entry.Job.Name}' run threw: {ex.Message}");
                // Make sure we don't leave RunningTask non-null on uncaught errors.
                await state.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { state.RunningTask = null; state.RunningCts = null; }
                finally { state.Gate.Release(); }
            }
        }, CancellationToken.None);

        await state.Gate.WaitAsync(outerCt).ConfigureAwait(false);
        try { state.RunningTask = task; }
        finally { state.Gate.Release(); }
        await Task.Yield();
    }

    private async Task HandleConcurrencyAsync(
        JobScheduleEntry entry,
        JobState state,
        DateTimeOffset due,
        JobConcurrencyPolicy policy,
        CancellationToken ct)
    {
        switch (policy)
        {
            case JobConcurrencyPolicy.Skip:
                state.LastFiredUtc = due;
                await WriteSkippedRecordAsync(entry, JobTrigger.Scheduled, due, ct).ConfigureAwait(false);
                break;

            case JobConcurrencyPolicy.Queue:
                if (state.QueuedTrigger is null && state.QueueDepth < _options.MaxQueueDepth)
                {
                    state.QueuedTrigger = JobTrigger.Scheduled;
                    state.QueueDepth   += 1;
                    state.LastFiredUtc  = due;
                }
                else
                {
                    state.LastFiredUtc = due;
                    await WriteSkippedRecordAsync(entry, JobTrigger.Scheduled, due, ct).ConfigureAwait(false);
                }
                break;

            case JobConcurrencyPolicy.KillAndRestart:
                state.RunningCts?.Cancel();
                try { if (state.RunningTask is not null) await state.RunningTask.ConfigureAwait(false); }
                catch { /* killed */ }
                state.LastFiredUtc = due;
                await DispatchAsync(entry, JobTrigger.Scheduled, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyCatchupAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        foreach (var entry in _source.Enumerate())
        {
            if (!CronEvaluator.TryParse(entry.Job.Schedule, out var schedule) || schedule is null) continue;

            var lastRun = _history.GetLastRunStartedAt(entry.ProjectPath, entry.Job.Name);
            if (lastRun is null) continue; // never ran — let the normal tick handle the first fire

            // Walk all occurrences between lastRun and now to find the most
            // recent missed slot. Fire ONE catch-up — the spec is "the last
            // missed occurrence", not "replay every missed slot". Hard cap
            // the walk so a misconfigured "every second" doesn't loop forever.
            DateTimeOffset? mostRecentMissed = null;
            var cursor = lastRun.Value;
            for (var i = 0; i < 100_000; i++)
            {
                var step = CronEvaluator.NextOccurrence(schedule, cursor, entry.Timezone);
                if (step is null || step > now) break;
                mostRecentMissed = step;
                cursor = step.Value;
            }
            if (mostRecentMissed is null) continue;

            // Pretend we already fired all the intermediate slots — anchor at
            // the latest missed one so the regular tick fires the next future
            // occurrence, not the historical ones we just collapsed.
            var key = KeyOf(entry);
            _states.GetOrAdd(key, _ => new JobState()).LastFiredUtc = mostRecentMissed.Value;

            await DispatchAsync(entry, JobTrigger.Catchup, ct).ConfigureAwait(false);
        }
    }

    private Task RecoverInterruptedRunsAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();
        foreach (var entry in _source.Enumerate())
        {
            if (seen.Add(entry.ProjectPath))
            {
                tasks.Add(_history.RecoverInterruptedAsync(entry.ProjectPath, ct));
            }
        }
        return Task.WhenAll(tasks);
    }

    private async Task WriteSkippedRecordAsync(JobScheduleEntry entry, JobTrigger trigger,
        DateTimeOffset due, CancellationToken ct)
    {
        var outcome = new JobRunOutcome(
            Status: JobRunStatus.Skipped,
            ExitCode: null,
            StartedAt: due,
            EndedAt: due,
            CommandLine: ClaudeJobArgBuilder.Render(
                "claude", ClaudeJobArgBuilder.Build(BuildRequest(entry, trigger))),
            StdoutInline: "",
            StdoutTruncated: false,
            StdoutSpilloverPath: null,
            Stderr: "previous run still active");
        await _history.RecordAsync(entry.ProjectPath, entry.ProjectName, entry.Job.Name,
            entry.Job.Prompt, trigger, outcome, ct).ConfigureAwait(false);
    }

    private static JobRunRequest BuildRequest(JobScheduleEntry entry, JobTrigger trigger) => new(
        ProjectPath: entry.ProjectPath,
        ProjectName: entry.ProjectName,
        JobName: entry.Job.Name,
        Prompt: entry.Job.Prompt,
        Trigger: trigger,
        TimeoutSeconds: entry.Job.TimeoutSeconds ?? 300,
        AllowedTools: entry.Job.AllowedTools,
        MaxTurns: entry.Job.MaxTurns,
        MaxBudgetUsd: entry.Job.MaxBudgetUsd,
        SkipPermissions: entry.Job.SkipPermissions ?? false);

    private static string KeyOf(JobScheduleEntry entry) =>
        $"{PlatformText.PathKey(entry.ProjectPath)}||{entry.Job.Name}";

    public async ValueTask DisposeAsync()
    {
        _lifecycle.Cancel();
        if (_tickLoop is not null)
        {
            try { await _tickLoop.ConfigureAwait(false); } catch { /* ignored */ }
        }

        // Cancel any in-flight runs.
        foreach (var state in _states.Values)
        {
            state.RunningCts?.Cancel();
        }
        _lifecycle.Dispose();
    }

    private sealed class JobState
    {
        public readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1);
        public DateTimeOffset? LastFiredUtc;
        public Task? RunningTask;
        public CancellationTokenSource? RunningCts;
        public JobTrigger? QueuedTrigger;
        public int QueueDepth;
    }
}
