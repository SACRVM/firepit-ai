namespace Firepit.Knowledge;

/// <summary>One search hit, already deduplicated to document granularity.</summary>
public sealed record KnowledgeHit(
    string Scope,
    string Path,
    string Title,
    string? Heading,
    string Snippet,
    double Score);

/// <summary>
/// Result of a (possibly multi-scope) search.
/// </summary>
/// <param name="Degraded">
/// The answer is not the full one: ranking fell back to full-text only in at
/// least one scope, or a scope could not be searched at all.
/// </param>
/// <param name="Warnings">
/// Everything the caller would otherwise have mistaken for "nothing found".
/// An empty result and an unsearched base look identical from the outside, and
/// the difference between them is whether the answer can be trusted — so a
/// scope that could not be searched, was never indexed, or failed its last
/// pass says so here rather than contributing silence.
/// </param>
/// <param name="DegradedReason">
/// Why ranking fell back to full-text, when it did. A sentence for the caller,
/// since the two causes need different responses: a model still downloading
/// resolves itself, a missing extension does not.
/// </param>
/// <param name="ScopesSearched">Scopes that answered, with or without vectors.</param>
/// <param name="ScopesFailed">Scopes that were asked and could not answer.</param>
public sealed record KnowledgeSearchResult(
    IReadOnlyList<KnowledgeHit> Hits,
    bool Degraded,
    IReadOnlyList<string>? Warnings = null,
    string? DegradedReason = null,
    int ScopesSearched = 0,
    int ScopesFailed = 0)
{
    /// <summary>Every scope asked failed, so the empty hit list means nothing.</summary>
    public bool NothingSearched => ScopesFailed > 0 && ScopesSearched == 0;

    public bool Trustworthy => Warnings is null || Warnings.Count == 0;
}

/// <summary>A full knowledge document as stored on disk.</summary>
public sealed record KnowledgeDocument(
    string Scope,
    string Path,
    string Title,
    string Content);

/// <summary>Outcome of one indexer pass over a scope.</summary>
/// <param name="Skipped">
/// Documents the pass could not read — locked by an editor mid-save, or gone
/// between the listing and the read. They are not in the index, so the pass
/// was incomplete and has to be repeated; a caller that treats it as complete
/// leaves those documents unfindable with nothing to show for it.
/// </param>
public sealed record IndexStats(
    int Indexed,
    int Unchanged,
    int Removed,
    int PendingEmbedding,
    int Skipped = 0)
{
    public static readonly IndexStats Empty = new(0, 0, 0, 0);

    public bool ChangedAnything => Indexed > 0 || Removed > 0;

    public bool Complete => Skipped == 0;
}
