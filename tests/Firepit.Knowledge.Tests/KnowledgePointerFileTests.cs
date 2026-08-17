namespace Firepit.Knowledge.Tests;

/// <summary>
/// Switching a project's knowledge location moves files around, so the rules
/// that keep it from losing any are the ones worth pinning down.
/// </summary>
public sealed class KnowledgePointerFileTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _meta;

    public KnowledgePointerFileTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-pointer-tests", Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "public-repo");
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
        Directory.CreateDirectory(_meta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Local => KnowledgeLayout.LocalDocsDir(_project);
    private string Hosted => KnowledgeLayout.HostedDocsDir(_meta, "public-repo");

    private void SeedLocalDoc(string name)
    {
        Directory.CreateDirectory(Local);
        File.WriteAllText(Path.Combine(Local, name), "# doc\n");
    }

    [Fact]
    public void Apply_MovesExistingDocsToTheNewHome()
    {
        SeedLocalDoc("finding.md");

        KnowledgePointerFile.Apply(_project, Hosted);

        Assert.True(File.Exists(Path.Combine(Hosted, "finding.md")));
        // The directory has to be gone — a pointer file needs its name.
        Assert.False(Directory.Exists(Local));
        Assert.True(File.Exists(Local));
    }

    [Fact]
    public void Apply_WritesARelativePathSoTheTreeCanMove()
    {
        KnowledgePointerFile.Apply(_project, Hosted);

        var text = File.ReadAllText(Local);
        Assert.Contains("../../.firepit/projects/public-repo/knowledge", text);
        Assert.DoesNotContain(_root, text);
    }

    [Fact]
    public void Apply_RoundTripsBackIntoTheRepo()
    {
        SeedLocalDoc("finding.md");
        KnowledgePointerFile.Apply(_project, Hosted);

        KnowledgePointerFile.Apply(_project, null);

        Assert.True(Directory.Exists(Local));
        Assert.True(File.Exists(Path.Combine(Local, "finding.md")));
        Assert.False(File.Exists(Path.Combine(Hosted, "finding.md")));
    }

    [Fact]
    public void Describe_RefusesToMergeTwoPopulatedBases()
    {
        SeedLocalDoc("finding.md");
        Directory.CreateDirectory(Hosted);
        File.WriteAllText(Path.Combine(Hosted, "finding.md"), "# a different doc\n");

        var plan = KnowledgePointerFile.Describe(_project, Hosted);

        Assert.False(plan.CanApply);
        Assert.Contains("already holds documents", plan.Blocker);
    }

    [Fact]
    public void Apply_RefusingToMerge_LeavesBothSidesUntouched()
    {
        SeedLocalDoc("finding.md");
        Directory.CreateDirectory(Hosted);
        File.WriteAllText(Path.Combine(Hosted, "finding.md"), "# a different doc\n");

        Assert.Throws<InvalidOperationException>(() => KnowledgePointerFile.Apply(_project, Hosted));

        Assert.Equal("# doc\n", File.ReadAllText(Path.Combine(Local, "finding.md")));
        Assert.Equal("# a different doc\n", File.ReadAllText(Path.Combine(Hosted, "finding.md")));
    }

    [Fact]
    public void Apply_JoiningAnExistingSharedBase_IsAllowedWhenNothingWouldBeOverwritten()
    {
        // The appkit case: four repos with no docs of their own point at a
        // base that already has some.
        Directory.CreateDirectory(Hosted);
        File.WriteAllText(Path.Combine(Hosted, "shared.md"), "# shared\n");

        KnowledgePointerFile.Apply(_project, Hosted);

        Assert.Equal(Hosted, KnowledgeLocator.Resolve(_project).DocsDir);
        Assert.True(File.Exists(Path.Combine(Hosted, "shared.md")));
    }

    [Fact]
    public void Apply_BackToTheRepoWithNoPointer_DoesNotDeleteTheDirectory()
    {
        SeedLocalDoc("finding.md");

        KnowledgePointerFile.Apply(_project, null);

        Assert.True(Directory.Exists(Local));
        Assert.True(File.Exists(Path.Combine(Local, "finding.md")));
    }
}

public sealed class KnowledgeLayoutTests : IDisposable
{
    private readonly string _root;
    private readonly string _meta;

    public KnowledgeLayoutTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "firepit-layout-tests", Guid.NewGuid().ToString("N"));
        _meta = Path.Combine(_root, ".firepit");
        Directory.CreateDirectory(_meta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void GlobalAndLocal_AreDifferentDirectories()
    {
        // The split this layout exists for: what the meta repo holds about
        // every project is not the meta project's own notes.
        Assert.NotEqual(
            KnowledgeLayout.GlobalDocsDir(_meta), KnowledgeLayout.LocalDocsDir(_meta));
    }

    [Fact]
    public void WhatTheMetaRepoHoldsAboutOthers_LivesOutsideItsOwnFirepitDir()
    {
        var hosted = KnowledgeLayout.HostedDocsDir(_meta, "color-bucket");

        Assert.StartsWith(Path.Combine(_meta, "projects"), hosted);
        Assert.DoesNotContain(Path.Combine(".firepit", ".firepit"), hosted);
    }

    [Fact]
    public void TheOldSharedDirectory_KeepsServingAsGlobalUntilItIsMoved()
    {
        // Starting an empty global base would silently demote every existing
        // doc to one project's local notes.
        Directory.CreateDirectory(KnowledgeLayout.LocalDocsDir(_meta));

        Assert.True(KnowledgeLayout.UsesLegacyGlobal(_meta));
        Assert.Equal(
            KnowledgeLayout.LocalDocsDir(_meta), KnowledgeLayout.ResolveGlobalDocsDir(_meta));
    }

    [Fact]
    public void OnceTheNewGlobalExists_TheOldPathIsJustLocalKnowledge()
    {
        Directory.CreateDirectory(KnowledgeLayout.LocalDocsDir(_meta));
        Directory.CreateDirectory(KnowledgeLayout.GlobalDocsDir(_meta));

        Assert.False(KnowledgeLayout.UsesLegacyGlobal(_meta));
        Assert.Equal(
            KnowledgeLayout.GlobalDocsDir(_meta), KnowledgeLayout.ResolveGlobalDocsDir(_meta));
    }
}
