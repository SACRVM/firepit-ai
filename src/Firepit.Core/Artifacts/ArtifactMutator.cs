namespace Firepit.Core.Artifacts;

/// <summary>
/// Pure list operations on a project's artifacts. Identity is the *resolved
/// absolute path*, not the stored string — linking <c>docs\report.md</c> after
/// <c>docs/report.md</c> must update the existing entry rather than produce a
/// duplicate that points at the same file.
///
/// Mirrors ProjectCommandMutator: no I/O here, so the semantics are unit-testable
/// without touching disk.
/// </summary>
public static class ArtifactMutator
{
    /// <summary>
    /// Insert <paramref name="entry"/>, or replace the existing link to the same
    /// file. Returns the new list and whether an existing entry was replaced.
    /// Newest-last ordering is preserved on insert; a replace keeps the original
    /// position so a re-added artifact doesn't jump around the pane.
    /// </summary>
    public static (IReadOnlyList<ArtifactEntry> Result, bool Replaced) Upsert(
        IReadOnlyList<ArtifactEntry>? existing,
        ArtifactEntry entry,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(projectPath);

        var list = existing is null ? [] : new List<ArtifactEntry>(existing);
        var target = ArtifactResolver.ToAbsolute(entry.Path, projectPath);

        for (var i = 0; i < list.Count; i++)
        {
            if (SamePath(list[i].Path, target, projectPath))
            {
                list[i] = entry;
                return (list, true);
            }
        }
        list.Add(entry);
        return (list, false);
    }

    /// <summary>
    /// Remove the link to <paramref name="path"/> (absolute or project-relative).
    /// Returns the new list and whether anything was removed — callers report
    /// "nothing to remove" rather than failing, so repeated calls are safe.
    /// </summary>
    public static (IReadOnlyList<ArtifactEntry> Result, bool Removed) RemoveByPath(
        IReadOnlyList<ArtifactEntry>? existing,
        string path,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(projectPath);

        if (existing is null || existing.Count == 0)
        {
            return ([], false);
        }

        var target = ArtifactResolver.ToAbsolute(path, projectPath);
        var result = new List<ArtifactEntry>(existing.Count);
        var removed = false;
        foreach (var candidate in existing)
        {
            if (!removed && SamePath(candidate.Path, target, projectPath))
            {
                removed = true;
                continue;
            }
            result.Add(candidate);
        }
        return (result, removed);
    }

    /// <summary>
    /// Remove by display label instead of path — the pane's context menu knows
    /// the label the user is looking at, and an agent that added "Bug report"
    /// shouldn't have to reconstruct the path to drop it again. First match wins.
    /// </summary>
    public static (IReadOnlyList<ArtifactEntry> Result, bool Removed) RemoveByLabel(
        IReadOnlyList<ArtifactEntry>? existing,
        string label,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(projectPath);

        if (existing is null || existing.Count == 0)
        {
            return ([], false);
        }

        var result = new List<ArtifactEntry>(existing.Count);
        var removed = false;
        foreach (var candidate in existing)
        {
            var candidateLabel = ArtifactResolver.Resolve(candidate, projectPath).Label;
            if (!removed && string.Equals(candidateLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }
            result.Add(candidate);
        }
        return (result, removed);
    }

    private static bool SamePath(string storedPath, string targetAbsolute, string projectPath) =>
        string.Equals(
            ArtifactResolver.ToAbsolute(storedPath, projectPath),
            targetAbsolute,
            StringComparison.OrdinalIgnoreCase);
}
