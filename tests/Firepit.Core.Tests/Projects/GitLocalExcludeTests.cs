using Firepit.Core.Projects;

namespace Firepit.Core.Tests.Projects;

public sealed class GitLocalExcludeTests : IDisposable
{
    private readonly string _root;

    public GitLocalExcludeTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-git-exclude-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ExcludePath => Path.Combine(_root, ".git", "info", "exclude");

    [Fact]
    public void Ensure_WithoutAGitDir_DoesNothing()
    {
        // Not every Firepit project is a repo, and creating a .git folder for
        // one would be a far bigger side effect than the caller asked for.
        Assert.False(GitLocalExclude.Ensure(_root, ".firepit/knowledge-pinned.md"));
        Assert.False(File.Exists(ExcludePath));
    }

    [Fact]
    public void Ensure_CreatesTheExcludeFileWhenGitHasNone()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        Assert.True(GitLocalExclude.Ensure(_root, ".firepit/knowledge-pinned.md"));
        Assert.Contains(".firepit/knowledge-pinned.md", File.ReadAllLines(ExcludePath));
    }

    [Fact]
    public void Ensure_IsIdempotent()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        Assert.True(GitLocalExclude.Ensure(_root, ".firepit/knowledge-pinned.md"));
        Assert.False(GitLocalExclude.Ensure(_root, ".firepit/knowledge-pinned.md"));

        Assert.Single(
            File.ReadAllLines(ExcludePath),
            l => l.Trim() == ".firepit/knowledge-pinned.md");
    }

    [Fact]
    public void Ensure_KeepsWhatGitAlreadyExcluded()
    {
        var infoDir = Path.Combine(_root, ".git", "info");
        Directory.CreateDirectory(infoDir);
        File.WriteAllLines(Path.Combine(infoDir, "exclude"), ["# git's own header", "*.local"]);

        GitLocalExclude.Ensure(_root, ".firepit/knowledge-pinned.md");

        var lines = File.ReadAllLines(ExcludePath);
        Assert.Contains("*.local", lines);
        Assert.Contains(".firepit/knowledge-pinned.md", lines);
    }
}
