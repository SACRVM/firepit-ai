namespace Firepit.Knowledge;

/// <param name="Inner">Scope whose documents sit inside another scope's directory.</param>
/// <param name="Outer">Scope that indexes them as its own.</param>
/// <param name="InnerDir">The nested documents directory.</param>
/// <param name="OuterDir">The directory containing it.</param>
public sealed record ScopeOverlap(string Inner, string Outer, string InnerDir, string OuterDir);

/// <summary>
/// Finds knowledge scopes whose documents directory is nested inside another's.
/// </summary>
/// <remarks>
/// <para>
/// A pointer file may aim anywhere, and nothing in <see cref="KnowledgeLocator"/>
/// can see this: it resolves one project at a time. The result is quiet
/// cross-contamination — the outer scope enumerates with
/// <c>SearchOption.AllDirectories</c>, so the inner scope's documents are
/// indexed into it as well, and a repo's private research becomes searchable
/// from every project that reads the outer base.
/// </para>
/// <para>
/// Equal directories are <b>not</b> an overlap. Several projects sharing one
/// base is the supported way to build a shared base; strict nesting is the
/// accident.
/// </para>
/// </remarks>
public static class ScopeOverlaps
{
    public static IReadOnlyList<ScopeOverlap> Find(IReadOnlyDictionary<string, string> docsDirs)
    {
        ArgumentNullException.ThrowIfNull(docsDirs);

        var found = new List<ScopeOverlap>();
        foreach (var (innerName, innerDir) in docsDirs)
        {
            foreach (var (outerName, outerDir) in docsDirs)
            {
                if (string.Equals(innerName, outerName, StringComparison.OrdinalIgnoreCase) ||
                    SameDirectory(innerDir, outerDir))
                {
                    continue;
                }

                if (IsUnder(outerDir, innerDir))
                {
                    found.Add(new ScopeOverlap(innerName, outerName, innerDir, outerDir));
                }
            }
        }

        return found;
    }

    public static bool SameDirectory(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is strictly below <paramref name="parent"/>.</summary>
    public static bool IsUnder(string parent, string candidate)
    {
        var p = Normalise(parent);
        var c = Normalise(candidate);

        // Strictly below. A drive or share root is its own prefix, so the
        // prefix test alone would call it its own parent.
        if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A root already ends in the separator; appending a second one would
        // make every child fail the prefix test.
        if (!p.EndsWith(Path.DirectorySeparatorChar))
        {
            p += Path.DirectorySeparatorChar;
        }

        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // An unusable path cannot be compared meaningfully. Returning it
            // rooted-or-not as given would silently compare a relative path
            // against absolute ones and report nothing; an empty string
            // compares equal to nothing and is under nothing, which is the
            // honest "no answer".
            return string.Empty;
        }
    }
}
