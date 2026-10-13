using System.IO;
using System.Text.Json;

namespace Firepit.Core.Jobs;

/// <summary>
/// File-system backed run history. Layout:
/// <code>
/// &lt;projectPath&gt;/.firepit/runs/
///     &lt;jobName&gt;/
///         2026-05-13T08-30-00Z.json
///         2026-05-13T09-00-00Z.json
///         stdout-&lt;guid&gt;.log     (only when StdoutTruncated)
/// </code>
/// Files are named after <c>StartedAt</c> (UTC, colon-safe). Reads walk the
/// directory; sorting by name == sorting chronologically.
///
/// <see cref="RecordAsync"/> writes atomically by writing to a <c>.tmp</c>
/// sibling and renaming. <see cref="RecoverInterruptedAsync"/> rewrites any
/// record missing <c>endedAt</c> to <c>Interrupted</c> — called once at
/// scheduler startup.
/// </summary>
public sealed class JsonJobHistoryStore : IJobHistoryStore
{
    private const string PortableInvalid = "\"<>|:*?\\/";

    public const string RunsDirectory = "runs";
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    private readonly TimeSpan _retention;
    private readonly Action<string>? _log;

    public JsonJobHistoryStore(TimeSpan? retention = null, Action<string>? log = null)
    {
        _retention = retention ?? DefaultRetention;
        _log = log;
    }

    public Task RecordAsync(string projectPath, string projectName, string jobName,
        string prompt, JobTrigger trigger, JobRunOutcome outcome, CancellationToken ct)
    {
        var jobDir = Path.Combine(projectPath, ".firepit", RunsDirectory, SanitizeJobName(jobName));
        Directory.CreateDirectory(jobDir);

        var record = ToRecord(jobName, projectName, prompt, trigger, outcome);
        var fileName = FormatFileName(outcome.StartedAt);
        var finalPath = Path.Combine(jobDir, fileName);
        var tempPath  = finalPath + ".tmp";

        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, record, JobRunJsonContext.Default.JobRunRecord);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"failed to write run record to {finalPath}: {ex.Message}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignored */ }
        }

        // Best-effort retention rotation on every write. Cheap (O(file count))
        // and runs out of the hot path because the runner has already exited.
        ApplyRetention(jobDir);

        return Task.CompletedTask;
    }

    public DateTimeOffset? GetLastRunStartedAt(string projectPath, string jobName)
    {
        var jobDir = Path.Combine(projectPath, ".firepit", RunsDirectory, SanitizeJobName(jobName));
        if (!Directory.Exists(jobDir)) return null;

        var newest = Directory.EnumerateFiles(jobDir, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
        return newest is null ? null : TryParseFileName(newest);
    }

    public async Task RecoverInterruptedAsync(string projectPath, CancellationToken ct)
    {
        var runsRoot = Path.Combine(projectPath, ".firepit", RunsDirectory);
        if (!Directory.Exists(runsRoot)) return;

        foreach (var file in Directory.EnumerateFiles(runsRoot, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            JobRunRecord? record;
            try
            {
                await using var stream = File.OpenRead(file);
                record = JsonSerializer.Deserialize(stream, JobRunJsonContext.Default.JobRunRecord);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"failed to read {file}: {ex.Message}");
                continue;
            }
            if (record is null) continue;
            if (record.EndedAt is not null) continue;

            var endedAt = DateTimeOffset.UtcNow;
            var patched = record with
            {
                Status     = JobRunStatus.Interrupted,
                EndedAt    = endedAt,
                DurationMs = (long)(endedAt - record.StartedAt).TotalMilliseconds,
                Stderr     = string.IsNullOrEmpty(record.Stderr)
                    ? "Firepit exited before this run completed."
                    : record.Stderr,
            };

            try
            {
                var tempPath = file + ".tmp";
                await using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, patched, JobRunJsonContext.Default.JobRunRecord);
                }
                File.Move(tempPath, file, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"failed to mark {file} interrupted: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<JobRunRecord> Load(string projectPath, string? jobName = null, int max = 100)
    {
        var runsRoot = Path.Combine(projectPath, ".firepit", RunsDirectory);
        if (!Directory.Exists(runsRoot)) return Array.Empty<JobRunRecord>();

        IEnumerable<string> files;
        if (jobName is not null)
        {
            var jobDir = Path.Combine(runsRoot, SanitizeJobName(jobName));
            if (!Directory.Exists(jobDir)) return Array.Empty<JobRunRecord>();
            files = Directory.EnumerateFiles(jobDir, "*.json", SearchOption.TopDirectoryOnly);
        }
        else
        {
            files = Directory.EnumerateFiles(runsRoot, "*.json", SearchOption.AllDirectories);
        }

        var ordered = files.OrderByDescending(f => f, StringComparer.Ordinal).Take(max);
        var records = new List<JobRunRecord>(max);
        foreach (var file in ordered)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var rec = JsonSerializer.Deserialize(stream, JobRunJsonContext.Default.JobRunRecord);
                if (rec is not null) records.Add(rec);
            }
            catch { /* skip unreadable */ }
        }
        return records;
    }

    private void ApplyRetention(string jobDir)
    {
        if (_retention <= TimeSpan.Zero) return;
        var cutoff = DateTimeOffset.UtcNow - _retention;
        foreach (var file in Directory.EnumerateFiles(jobDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var ts = TryParseFileName(name);
            if (ts is null || ts.Value > cutoff) continue;
            try { File.Delete(file); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private static JobRunRecord ToRecord(string jobName, string projectName, string prompt,
        JobTrigger trigger, JobRunOutcome outcome) => new(
        Version:             JobRunRecord.CurrentVersion,
        JobName:             jobName,
        ProjectName:         projectName,
        Trigger:             trigger,
        Status:              outcome.Status,
        StartedAt:           outcome.StartedAt,
        EndedAt:             outcome.EndedAt,
        DurationMs:          (long)outcome.Duration.TotalMilliseconds,
        ExitCode:            outcome.ExitCode,
        Prompt:              prompt,
        CommandLine:         outcome.CommandLine,
        StdoutInline:        outcome.StdoutInline,
        StdoutTruncated:     outcome.StdoutTruncated,
        StdoutSpilloverPath: outcome.StdoutSpilloverPath,
        Stderr:              outcome.Stderr,
        TokensInput:         outcome.ClaudeMetadata?.TokensInput,
        TokensOutput:        outcome.ClaudeMetadata?.TokensOutput,
        CostUsd:             outcome.ClaudeMetadata?.CostUsd,
        Summary:             outcome.ClaudeMetadata?.Summary,
        AssistantMessage:    outcome.ClaudeMetadata?.AssistantMessage);

    /// <summary>
    /// "2026-05-13T08:30:00.123Z" → "2026-05-13T08-30-00-123Z.json".
    /// Millisecond precision in the filename prevents collisions when two runs
    /// share the same second — e.g. a kill-and-restart cycle or a manual
    /// trigger that lands during a scheduled fire. Sortable as a string =
    /// chronological order, same as before.
    /// </summary>
    public static string FormatFileName(DateTimeOffset startedAt) =>
        startedAt.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ss-fffZ",
            System.Globalization.CultureInfo.InvariantCulture) + ".json";

    public static DateTimeOffset? TryParseFileName(string fileName)
    {
        var ts = Path.GetFileNameWithoutExtension(fileName);

        // Current format with milliseconds.
        if (DateTime.TryParseExact(ts, "yyyy-MM-ddTHH-mm-ss-fffZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var withMs))
        {
            return new DateTimeOffset(withMs, TimeSpan.Zero);
        }

        // Backwards-compat: records written by pre-fix builds used second precision.
        if (DateTime.TryParseExact(ts, "yyyy-MM-ddTHH-mm-ssZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var legacy))
        {
            return new DateTimeOffset(legacy, TimeSpan.Zero);
        }
        return null;
    }

    /// <summary>
    /// Job names go straight into a directory path. Strip characters that
    /// would explode on Windows; leave dashes / underscores / dots alone.
    /// The Windows set on every OS: Linux forbids only '/' and NUL, and a run
    /// folder named "with:colon" would not survive a checkout on Windows.
    /// </summary>
    public static string SanitizeJobName(string jobName)
    {
        var clean = string.Concat(jobName.Select(ch => ch < 32 || PortableInvalid.Contains(ch) ? '_' : ch));
        return clean.Length == 0 ? "_" : clean;
    }
}
