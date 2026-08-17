namespace Firepit.Knowledge.Tests;

/// <summary>
/// A pointer may aim anywhere, including inside another scope's documents
/// directory. Nothing resolving one project at a time can see that, and the
/// consequence is quiet: the outer scope indexes the inner one's files as its
/// own, so a project's private research becomes searchable from every project
/// that reads the outer base.
/// </summary>
public class ScopeOverlapTests
{
    private static IReadOnlyList<(string Inner, string Outer)> Overlaps(
        params (string Name, string Dir)[] scopes)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in scopes)
        {
            map[name] = dir;
        }

        return [.. ScopeOverlaps.Find(map).Select(o => (o.Inner, o.Outer))];
    }

    private static string P(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "repos", .. parts]);

    [Fact]
    public void APointerInsideTheGlobalBase_IsAnOverlap()
    {
        // The real case: a pointer aimed at <meta>/knowledge/<project> lands
        // inside the global documents directory, which enumerates recursively.
        var overlaps = Overlaps(
            ("global", P(".firepit", "knowledge")),
            ("grok-mcp", P(".firepit", "knowledge", "grok-mcp")));

        Assert.Single(overlaps);
        Assert.Equal(("grok-mcp", "global"), overlaps[0]);
    }

    [Fact]
    public void TheOverlapNamesBothDirectories()
    {
        // The finding is reported to both scopes, and each half needs the other
        // side's path to say anything actionable.
        var inner = P(".firepit", "knowledge", "grok-mcp");
        var outer = P(".firepit", "knowledge");

        var found = ScopeOverlaps.Find(new Dictionary<string, string>
        {
            ["global"] = outer,
            ["grok-mcp"] = inner,
        });

        Assert.Equal(inner, Assert.Single(found).InnerDir);
        Assert.Equal(outer, found[0].OuterDir);
    }

    [Fact]
    public void TheConventionalHostedLayout_IsNotAnOverlap()
    {
        // <meta>/projects/<name>/knowledge is a sibling of <meta>/knowledge,
        // which is exactly why the layout puts hosted stores under projects/.
        var overlaps = Overlaps(
            ("global", P(".firepit", "knowledge")),
            ("grok-mcp", P(".firepit", "projects", "grok-mcp", "knowledge")));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void SeveralProjectsSharingOneDirectory_IsNotAnOverlap()
    {
        // The appkit case, and the whole point of shared bases. Equal is fine;
        // only strict nesting contaminates.
        var shared = P(".firepit", "projects", "appkit", "knowledge");
        var overlaps = Overlaps(
            ("global", P(".firepit", "knowledge")),
            ("appkit", shared),
            ("sacrvm-desktop", shared),
            ("sacrvm-notes", shared));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ProjectsKeepingTheirOwnKnowledge_DoNotOverlap()
    {
        var overlaps = Overlaps(
            ("global", P(".firepit", "knowledge")),
            (".firepit", P(".firepit", ".firepit", "knowledge")),
            ("firepit-ai", P("firepit-ai", ".firepit", "knowledge")));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ATrailingSeparator_DoesNotInventAnOverlap()
    {
        var shared = P(".firepit", "projects", "appkit", "knowledge");
        var overlaps = Overlaps(
            ("appkit", shared),
            ("sacrvm-notes", shared + Path.DirectorySeparatorChar));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ASiblingWithASharedPrefix_IsNotAnOverlap()
    {
        // "…/knowledge" must not read as the parent of "…/knowledge-archive".
        var overlaps = Overlaps(
            ("a", P(".firepit", "projects", "knowledge")),
            ("b", P(".firepit", "projects", "knowledge-archive")));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void ADriveRoot_StillContainsItsChildren()
    {
        // TrimEndingDirectorySeparator leaves a root alone, so appending a
        // separator unconditionally would produce "C:\\" and match nothing.
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.True(ScopeOverlaps.IsUnder(root, P("firepit-ai", ".firepit", "knowledge")));
        Assert.False(ScopeOverlaps.IsUnder(root, root));
    }

    [Fact]
    public void AnUnusablePath_ReportsNothingRatherThanGuessing()
    {
        var overlaps = Overlaps(
            ("broken", "\0not a path"),
            ("real", P("firepit-ai", ".firepit", "knowledge")));

        Assert.Empty(overlaps);
    }
}
