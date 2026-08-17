using Firepit.Core.Blueprints;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.Blueprints;

public sealed class BlueprintApplierTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly Blueprint _blueprint;

    public BlueprintApplierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-blueprint-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "some-project");
        Directory.CreateDirectory(Path.Combine(_root, ".firepit"));
        Directory.CreateDirectory(_project);

        var store = new BlueprintStore(_root);
        store.EnsureDefaults();
        _blueprint = store.TryLoad("firepit")!;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Apply_OnBareProject_CreatesEverything_ThenIsIdempotent()
    {
        var first = BlueprintApplier.Apply(_blueprint, _project, "some-project");

        Assert.NotEmpty(first.Actions);
        Assert.True(File.Exists(Path.Combine(_project, ".firepit", "config.json")));
        Assert.True(File.Exists(Path.Combine(_project, ".firepit", "knowledge", "README.md")));
        Assert.True(File.Exists(Path.Combine(_project, ".firepit", "knowledge-pinned.md")));
        var gitignore = File.ReadAllText(Path.Combine(_project, ".gitignore"));
        Assert.Contains(".firepit/knowledge.db*", gitignore, StringComparison.Ordinal);
        var claudeMd = File.ReadAllText(Path.Combine(_project, "CLAUDE.md"));
        Assert.Contains("firepit_inbox_complete", claudeMd, StringComparison.Ordinal);
        Assert.Contains("firepit_knowledge_search", claudeMd, StringComparison.Ordinal);
        Assert.Contains("@.firepit/knowledge-pinned.md", claudeMd, StringComparison.Ordinal);

        // Same operation, second run: nothing left to do.
        var second = BlueprintApplier.Apply(_blueprint, _project, "some-project");
        Assert.Empty(second.Actions);
        Assert.True(BlueprintApplier.Check(_blueprint, _project).Conformant);
    }

    [Fact]
    public void Check_OnBareProject_ReportsEverythingPending()
    {
        var check = BlueprintApplier.Check(_blueprint, _project);

        Assert.False(check.Conformant);
        Assert.True(check.MissingProjectConfig);
        Assert.Contains(".firepit/knowledge/README.md", check.MissingFiles);
        Assert.Contains(".firepit/knowledge-pinned.md", check.MissingFiles);
        Assert.NotEmpty(check.MissingGitignoreLines);
        Assert.Equal(5, check.MissingClaudeMdSections.Count);
        Assert.NotEmpty(check.DescribePending());
    }

    [Fact]
    public void Check_DoesNotModifyTheProject()
    {
        BlueprintApplier.Check(_blueprint, _project);

        Assert.False(Directory.Exists(Path.Combine(_project, ".firepit")));
        Assert.False(File.Exists(Path.Combine(_project, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(_project, "CLAUDE.md")));
    }

    [Fact]
    public void Apply_NeverOverwritesExistingFiles()
    {
        var readmePath = Path.Combine(_project, ".firepit", "knowledge", "README.md");
        Directory.CreateDirectory(Path.GetDirectoryName(readmePath)!);
        File.WriteAllText(readmePath, "# My own conventions\n");

        BlueprintApplier.Apply(_blueprint, _project, "some-project");

        Assert.Equal("# My own conventions\n", File.ReadAllText(readmePath));
    }

    [Fact]
    public void Apply_AppendsSectionsToExistingClaudeMd_WithoutTouchingTheRest()
    {
        File.WriteAllText(Path.Combine(_project, "CLAUDE.md"), "# some-project\n\nExisting guidance.\n");

        BlueprintApplier.Apply(_blueprint, _project, "some-project");

        var claudeMd = File.ReadAllText(Path.Combine(_project, "CLAUDE.md"));
        Assert.StartsWith("# some-project", claudeMd, StringComparison.Ordinal);
        Assert.Contains("Existing guidance.", claudeMd, StringComparison.Ordinal);
        Assert.Contains("firepit_knowledge_search", claudeMd, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_BlanketIgnore_WarnsByDefault_FixesWhenOptedIn()
    {
        File.WriteAllText(Path.Combine(_project, ".gitignore"), ".firepit/\n");

        var warned = BlueprintApplier.Apply(_blueprint, _project, "some-project");
        Assert.Contains(warned.Warnings, w => w.Contains(".firepit/", StringComparison.Ordinal));
        Assert.Contains(".firepit/\n", File.ReadAllText(Path.Combine(_project, ".gitignore")), StringComparison.Ordinal);

        var fixedRun = BlueprintApplier.Apply(_blueprint, _project, "some-project", fixBlanketIgnores: true);
        Assert.Contains(fixedRun.Actions, a => a.Contains("blanket ignore", StringComparison.Ordinal));
        var gitignore = File.ReadAllText(Path.Combine(_project, ".gitignore"));
        Assert.Contains("# .firepit/", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshScaffold_IsBlueprintConformantFromBirth()
    {
        // The invariant that ties ProjectScaffolding and the default
        // blueprint together: a project Firepit scaffolds today needs no
        // modernisation tomorrow. The meta path is part of scaffolding now —
        // the shared-fragment imports cannot be written without knowing where
        // the central repo is, and Firepit always does.
        ProjectScaffolding.EnsureProjectScaffold(
            _project, "some-project", metaProjectPath: Path.Combine(_root, ".firepit"));

        var check = BlueprintApplier.Check(_blueprint, _project);

        Assert.True(check.Conformant, string.Join("; ", check.DescribePending()));
    }
}
