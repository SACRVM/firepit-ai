using Firepit.Core.Blueprints;
using Firepit.Core.Projects;

namespace Firepit.Core.Tests.Blueprints;

/// <summary>
/// Auditing which policy fragment a project imports. The subtle half is what
/// happens when the repository's visibility cannot be read at all.
/// </summary>
public sealed class FragmentIntegrityTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _meta;

    public FragmentIntegrityTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-fragment-integrity", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(_project);
        FirepitFragments.EnsureSeeded(_meta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteClaudeMd(string body) =>
        File.WriteAllText(Path.Combine(_project, "CLAUDE.md"), body);

    private IReadOnlyList<FragmentFinding> Check() => FragmentIntegrity.Check(_project, _meta);

    [Fact]
    public void NoClaudeMd_IsNotAFinding()
    {
        Assert.Empty(Check());
    }

    [Fact]
    public void NoFragmentSection_IsAWarningNotAnError()
    {
        WriteClaudeMd("# Project\n\nNothing shared here.\n");

        var finding = Assert.Single(Check());
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void AProjectNotOnGitHub_IsNotAskedAboutVisibility()
    {
        // No .git at all is an answer, not a guess: importing a public/private
        // policy fragment here is drift, and saying so is correct.
        WriteClaudeMd(
            $"{FirepitFragments.SectionMarker}\n" +
            $"@../.firepit/{FirepitFragments.DirName}/{FirepitFragments.SharedFileName}\n" +
            $"@../.firepit/{FirepitFragments.DirName}/{FirepitFragments.PrivateFileName}\n");

        Assert.Contains(Check(), f => f.Message.Contains("not on GitHub"));
        Assert.DoesNotContain(Check(), f => f.Severity == "error");
    }

    [Fact]
    public void AnUnreadableVisibility_NeverProducesAnError()
    {
        // A .git with a github.com remote, but `gh` cannot answer here: no
        // auth, no network, or not installed. The fail-safe guess is PUBLIC,
        // and auditing against a guess would tell a correctly configured
        // private repo that it is dangerously misconfigured.
        var gitDir = Path.Combine(_project, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(
            Path.Combine(gitDir, "config"),
            "[remote \"origin\"]\n\turl = https://github.com/example/nope-does-not-exist.git\n");

        WriteClaudeMd(
            $"{FirepitFragments.SectionMarker}\n" +
            $"@../.firepit/{FirepitFragments.DirName}/{FirepitFragments.SharedFileName}\n" +
            $"@../.firepit/{FirepitFragments.DirName}/{FirepitFragments.PrivateFileName}\n");

        var findings = Check();
        var visibility = GitHubVisibility.Inspect(_project);

        if (visibility.Certain)
        {
            // gh answered — the repository really is public, and the error is
            // the correct one. Nothing to assert about the unknown path.
            Assert.Equal(RepoVisibility.Public, visibility.Value);
            return;
        }

        Assert.DoesNotContain(findings, f => f.Severity == "error");
        Assert.Contains(findings, f => f.Message.Contains("could not be read"));
    }

    [Fact]
    public void NotOnGitHub_IsAlwaysACertainAnswer()
    {
        Assert.True(GitHubVisibility.Inspect(_project).Certain);
        Assert.Equal(RepoVisibility.None, GitHubVisibility.Inspect(_project).Value);
    }
}
