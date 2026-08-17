using Firepit.Core.Blueprints;

namespace Firepit.Core.Tests.Blueprints;

public sealed class FirepitFragmentsTests : IDisposable
{
    private readonly string _root;
    private readonly string _meta;

    public FirepitFragmentsTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-fragment-tests", Guid.NewGuid().ToString("N"));
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(_meta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheyLiveWhereTheCentralRepoKeepsWhatBelongsToOtherProjects()
    {
        // Not in <meta>/.firepit/, which is the meta project's own space, and
        // not as <meta>/CLAUDE.md, which already addresses an agent working
        // inside that repo.
        Assert.Equal(Path.Combine(_meta, "projects"), FirepitFragments.DirectoryFor(_meta));
        Assert.Equal(
            Path.Combine(_meta, "projects", "claude.md"), FirepitFragments.SharedPath(_meta));
    }

    [Fact]
    public void ImportPath_IsRelativeForAProjectNextToTheCentralRepo()
    {
        var project = Path.Combine(_root, "color-bucket");

        var import = FirepitFragments.ImportPath(project, FirepitFragments.SharedPath(_meta));

        // A relative import survives the whole tree being moved.
        Assert.Equal("../.firepit/projects/claude.md", import);
    }

    [Fact]
    public void ImportPath_IsAbsoluteForAProjectThatIsNotNextToIt()
    {
        // music-lib lives on a network share; no relative path reaches it.
        var faraway = Path.Combine(_root, "deep", "deeper", "project");

        var import = FirepitFragments.ImportPath(faraway, FirepitFragments.SharedPath(_meta));

        Assert.True(Path.IsPathRooted(import), import);
    }

    [Fact]
    public void EnsureSeeded_WritesTheThreeFragments()
    {
        var created = FirepitFragments.EnsureSeeded(_meta);

        Assert.Equal(3, created.Count);
        Assert.True(File.Exists(FirepitFragments.SharedPath(_meta)));
        Assert.True(File.Exists(FirepitFragments.ClassPath(_meta, isPublic: true)));
        Assert.True(File.Exists(FirepitFragments.ClassPath(_meta, isPublic: false)));
    }

    [Fact]
    public void EnsureSeeded_NeverOverwritesAnEdit()
    {
        // The whole point of a fragment is that she can change it. An edit
        // that gets reset on the next launch is worse than no fragment.
        FirepitFragments.EnsureSeeded(_meta);
        File.WriteAllText(FirepitFragments.SharedPath(_meta), "# my own rules\n");

        var created = FirepitFragments.EnsureSeeded(_meta);

        Assert.Empty(created);
        Assert.Equal("# my own rules\n", File.ReadAllText(FirepitFragments.SharedPath(_meta)));
    }

    [Fact]
    public void ResolveSection_LeavesNoTokensBehind()
    {
        var resolved = FirepitFragments.ResolveSection(
            FirepitBlueprintDefaults.FragmentsSection, Path.Combine(_root, "color-bucket"), _meta);

        Assert.DoesNotContain(FirepitFragments.SharedToken, resolved);
        Assert.DoesNotContain(FirepitFragments.ClassToken, resolved);
        Assert.Contains("@../.firepit/projects/claude.md", resolved);
    }

    [Fact]
    public void ResolveSection_OmitsTheClassImportForAProjectThatIsNotOnGitHub()
    {
        // music-lib sits on a network share and is not a repo at all. Telling
        // its agent "anything committed here is readable by anyone" is not
        // cautious, it is false — so it gets no class fragment.
        var notARepo = Path.Combine(_root, "music-lib");
        Directory.CreateDirectory(notARepo);

        var resolved = FirepitFragments.ResolveSection(
            FirepitBlueprintDefaults.FragmentsSection, notARepo, _meta);

        Assert.DoesNotContain("claude-github", resolved);
        Assert.DoesNotContain(FirepitFragments.ClassToken, resolved);
        // The shared fragment still applies — it is hosted in Firepit either way.
        Assert.Contains("claude.md", resolved);
    }

    [Fact]
    public void ResolveSection_LeavesTokenFreeContentAlone()
    {
        var untouched = FirepitFragments.ResolveSection(
            FirepitBlueprintDefaults.InboxSection, Path.Combine(_root, "color-bucket"), _meta);

        Assert.Equal(FirepitBlueprintDefaults.InboxSection, untouched);
    }

    [Fact]
    public void TheFragmentsDoNotRestateWhatTheHandshakeAlreadyCarries()
    {
        // Policy here, behaviour in the MCP instructions. Two copies of the
        // same conventions is the duplication this split exists to avoid.
        foreach (var fragment in new[]
        {
            FirepitFragments.SharedFragment,
            FirepitFragments.PublicFragment,
            FirepitFragments.PrivateFragment,
        })
        {
            Assert.DoesNotContain("firepit_artifact_list", fragment);
            Assert.DoesNotContain("firepit_inbox_complete", fragment);
        }
    }
}
