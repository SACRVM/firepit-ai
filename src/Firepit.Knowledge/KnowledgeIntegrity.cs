namespace Firepit.Knowledge;

/// <summary>
/// What an integrity check found about one knowledge scope.
/// </summary>
/// <param name="DocsOnDisk">Markdown files under the scope's documents directory.</param>
/// <param name="DocsIndexed">Rows in the index's document manifest.</param>
/// <param name="MissingFromIndex">
/// On disk but not in the index — the silent-loss case. These documents exist
/// and cannot be found by searching for them.
/// </param>
/// <param name="StaleInIndex">
/// In the index but no longer on disk. Harmless to a search's correctness but a
/// sign the index has stopped tracking.
/// </param>
/// <param name="OutOfDate">
/// In both, but the file's content no longer matches the hash that was indexed.
/// Searches answer from the old text.
/// </param>
public sealed record ScopeIntegrity(
    string Scope,
    string DocsDir,
    ScopeHealth Health,
    int DocsOnDisk,
    int DocsIndexed,
    IReadOnlyList<string> MissingFromIndex,
    IReadOnlyList<string> StaleInIndex,
    IReadOnlyList<string> OutOfDate,
    string? IndexError = null,
    IReadOnlyList<string>? Repairs = null)
{
    /// <summary>
    /// Whether the index can stand in for the documents. Deliberately strict:
    /// a scope that is merely <i>probably</i> current is the state this whole
    /// check exists to stop treating as fine.
    /// </summary>
    public bool Sound =>
        IndexError is null &&
        Health is ScopeHealth.Ready &&
        MissingFromIndex.Count == 0 &&
        OutOfDate.Count == 0 &&
        StaleInIndex.Count == 0;

    public IReadOnlyList<string> Describe()
    {
        var findings = new List<string>();
        if (IndexError is { } err)
        {
            findings.Add($"index unreadable: {err}");
        }

        if (Health is not ScopeHealth.Ready)
        {
            findings.Add($"index state is {Health}");
        }

        if (MissingFromIndex.Count > 0)
        {
            findings.Add(
                $"{MissingFromIndex.Count} document(s) on disk are not in the index and cannot be found: " +
                string.Join(", ", MissingFromIndex.Take(5)));
        }

        if (OutOfDate.Count > 0)
        {
            findings.Add(
                $"{OutOfDate.Count} document(s) changed since indexing; searches answer from the old text: " +
                string.Join(", ", OutOfDate.Take(5)));
        }

        if (StaleInIndex.Count > 0)
        {
            findings.Add($"{StaleInIndex.Count} indexed document(s) no longer exist on disk");
        }

        return findings;
    }
}
