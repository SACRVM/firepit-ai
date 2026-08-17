using Firepit.Core.Blueprints;
using Firepit.Core.Projects;

namespace Firepit.Core.Tests.Blueprints;

/// <summary>
/// The rule the integrity check was missing: documents in a defensible place.
/// A scope can be perfectly indexed and still sit in a repository anyone can
/// read, which is how twelve public repos passed every check while holding a
/// knowledge base.
/// </summary>
public sealed class KnowledgePlacementTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _meta;

    public KnowledgePlacementTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-placement", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "public-repo");
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A repo that reads as public with certainty, without a network.</summary>
    private void MakePublicRepo()
    {
        var gitDir = Path.Combine(_project, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config"),
            "[remote \"origin\"]\n\turl = https://github.com/example/thing.git\n");
    }

    private IReadOnlyList<KnowledgePlacement.Finding> Check(
        bool digestTracked = false, RepoVisibility? seen = null, bool certain = true) =>
        KnowledgePlacement.Check(
            _project, "public-repo", _meta,
            isTracked: (_, _) => digestTracked,
            // Injected: asking gh about a fake remote answers "could not tell",
            // which is a real state but not the one under test here.
            visibility: _ => new VisibilityResult(seen ?? RepoVisibility.None, certain));

    private void WriteDoc(string name) =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(
                Path.Combine(_project, ".firepit", "knowledge")).FullName, name),
            "# Doc\n\nbody\n");

    private void WritePointer() =>
        File.WriteAllText(
            Path.Combine(_project, ".firepit", "knowledge"), @"..\..\.firepit\projects\x\knowledge");

    [Fact]
    public void AProjectNotOnGitHub_IsNotJudgedOnVisibility()
    {
        WriteDoc("finding.md");

        Assert.Empty(Check());
    }

    [Fact]
    public void APublicRepoHoldingDocuments_IsAnError()
    {
        MakePublicRepo();
        WriteDoc("finding.md");

        var f = Check(seen: RepoVisibility.Public).Single(x => x.Message.Contains("knowledge document"));
        Assert.Equal("error", f.Severity);
        Assert.Contains("readable by anyone", f.Message);
    }

    [Fact]
    public void APublicRepoWithAnEmptyBase_IsAWarningNotAnError()
    {
        // Nothing exposed yet — but the next document saved would be, and that
        // is the moment nobody notices.
        MakePublicRepo();
        Directory.CreateDirectory(Path.Combine(_project, ".firepit", "knowledge"));
        File.WriteAllText(
            Path.Combine(_project, ".firepit", "knowledge", "README.md"), "seed\n");

        var f = Assert.Single(Check(seen: RepoVisibility.Public));
        Assert.Equal("warning", f.Severity);
        Assert.Contains("nothing is exposed yet", f.Message);
    }

    [Fact]
    public void APublicRepoBehindAPointer_IsClean()
    {
        MakePublicRepo();
        WritePointer();

        Assert.Empty(Check(seen: RepoVisibility.Public));
    }

    [Fact]
    public void ATrackedDigestInARedirectedPublicRepo_IsAnError()
    {
        // Generated from documents that deliberately live elsewhere, so
        // committing it carries their text back into a public repo.
        WritePointer();

        var f = Assert.Single(Check(digestTracked: true, seen: RepoVisibility.Public));
        Assert.Equal("error", f.Severity);
        Assert.Contains("anyone can read", f.Message);
    }

    [Fact]
    public void ATrackedDigestInARedirectedPrivateRepo_IsOnlyAWarning()
    {
        // Same rule, different stakes: derived data churning in git, not a
        // leak. Grading it as an error would dilute the ones that are.
        WritePointer();

        var f = Assert.Single(Check(digestTracked: true, seen: RepoVisibility.Private));
        Assert.Equal("warning", f.Severity);
    }

    [Fact]
    public void AnUntrackedDigestInARedirectedRepo_IsClean()
    {
        WritePointer();

        Assert.Empty(Check(digestTracked: false));
    }

    [Fact]
    public void ATrackedDigestInANormalRepo_IsFine()
    {
        // Not redirected: the digest is compiled from documents in this very
        // repo, and committing it is the intended behaviour.
        WriteDoc("finding.md");

        Assert.Empty(Check(digestTracked: true));
    }
}
