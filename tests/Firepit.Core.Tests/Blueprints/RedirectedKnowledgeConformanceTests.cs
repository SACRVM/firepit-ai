using Firepit.Core.Blueprints;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.Blueprints;

/// <summary>
/// A project whose knowledge is redirected must not be told it is missing the
/// files that would live in the directory it no longer has. Reporting them
/// sends an agent to run an apply that replaces the pointer with a directory —
/// and a public repo starts committing its research again.
/// </summary>
public sealed class RedirectedKnowledgeConformanceTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly Blueprint _blueprint;

    public RedirectedKnowledgeConformanceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-redirect-conformance", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "public-repo");
        Directory.CreateDirectory(Path.Combine(_root, ".firepit"));
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));

        var store = new BlueprintStore(_root);
        store.EnsureDefaults();
        _blueprint = store.TryLoad("firepit")!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string KnowledgePath => Path.Combine(_project, ".firepit", "knowledge");

    private void WritePointer() =>
        File.WriteAllText(KnowledgePath, "../../.firepit/projects/public-repo/knowledge\n");

    [Fact]
    public void IsRedirected_IsTrueOnlyForAFile()
    {
        Assert.False(KnowledgeRedirect.IsRedirected(_project));

        Directory.CreateDirectory(KnowledgePath);
        Assert.False(KnowledgeRedirect.IsRedirected(_project));

        Directory.Delete(KnowledgePath);
        WritePointer();
        Assert.True(KnowledgeRedirect.IsRedirected(_project));
    }

    [Fact]
    public void Check_DoesNotReportTheKnowledgeReadmeAsMissing_WhenRedirected()
    {
        WritePointer();

        var check = BlueprintApplier.Check(_blueprint, _project);

        Assert.DoesNotContain(".firepit/knowledge/README.md", check.MissingFiles);
        // The digest still belongs to the project, redirect or not.
        Assert.Contains(".firepit/knowledge-pinned.md", check.MissingFiles);
    }

    [Fact]
    public void Check_StillReportsIt_WhenTheProjectKeepsItsOwnKnowledge()
    {
        var check = BlueprintApplier.Check(_blueprint, _project);

        Assert.Contains(".firepit/knowledge/README.md", check.MissingFiles);
    }

    [Fact]
    public void Apply_LeavesThePointerFileIntact()
    {
        // The defect in full: an apply used to turn the pointer back into a
        // directory, silently undoing the redirect.
        WritePointer();
        var before = File.ReadAllText(KnowledgePath);

        BlueprintApplier.Apply(_blueprint, _project, "public-repo");

        Assert.True(File.Exists(KnowledgePath));
        Assert.False(Directory.Exists(KnowledgePath));
        Assert.Equal(before, File.ReadAllText(KnowledgePath));
    }

    [Fact]
    public void Scaffold_LeavesThePointerFileIntact()
    {
        WritePointer();
        var before = File.ReadAllText(KnowledgePath);

        ProjectScaffolding.EnsureProjectScaffold(_project, "public-repo");

        Assert.False(Directory.Exists(KnowledgePath));
        Assert.Equal(before, File.ReadAllText(KnowledgePath));
    }

    [Fact]
    public void EnsureKnowledgeReadme_RefusesOnItsOwn()
    {
        // Guarded inside, not only at the call site: whoever calls this next
        // should not be able to undo a redirect by accident.
        WritePointer();

        Assert.False(ProjectScaffolding.EnsureKnowledgeReadme(_project));
        Assert.False(Directory.Exists(KnowledgePath));
    }

    [Fact]
    public void ARedirectedProject_ReachesConformance()
    {
        // The end state that matters: after an apply, the check comes back
        // clean instead of pointing at a fix that would break things.
        WritePointer();

        BlueprintApplier.Apply(_blueprint, _project, "public-repo");
        var check = BlueprintApplier.Check(_blueprint, _project);

        Assert.True(check.Conformant, string.Join("; ", check.DescribePending()));
    }
}
