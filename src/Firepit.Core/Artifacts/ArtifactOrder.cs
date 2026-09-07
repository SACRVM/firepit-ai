namespace Firepit.Core.Artifacts;

/// <summary>
/// Display order for the artifact pane: newest at the top.
/// </summary>
/// <remarks>
/// <para>
/// Storage stays append-ordered — <see cref="ArtifactMutator.Upsert"/> adds to
/// the end and a replace keeps its original slot, so the file reads as the
/// history of what was pinned. The pane wants the opposite: a session pins as
/// it works, and the thing worth looking at is what was just produced. Left in
/// storage order it arrives below everything from every earlier session, which
/// is a scroll for the one entry the user is actually after.
/// </para>
/// <para>
/// The reversal is deliberately positional rather than a sort on
/// <c>AddedAtUtc</c>. Append order is chronological by construction, and it is
/// defined for every entry — including ones hand-written into the JSON, which
/// carry no timestamp. Sorting on the timestamp would have to invent a place
/// for those, and would silently reorder the list the day a clock disagrees.
/// The timestamp stays what it is: a record of when something was pinned, not
/// the thing the order depends on.
/// </para>
/// <para>
/// A replaced entry keeping its slot is preserved on purpose. Re-pinning a file
/// updates it in place instead of throwing it to the top, so a pane the user
/// has learned the shape of does not rearrange itself under them.
/// </para>
/// </remarks>
public static class ArtifactOrder
{
    /// <summary>Newest first, for display. Does not touch what is stored.</summary>
    public static IReadOnlyList<T> NewestFirst<T>(IReadOnlyList<T>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        var result = new List<T>(entries.Count);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            result.Add(entries[i]);
        }
        return result;
    }
}
