using System.IO;
using Firepit.Core.Artifacts;

namespace Firepit.Core.Tests.Artifacts;

public class ArtifactResolverTests
{
    private const string ProjectPath = @"C:\repos\demo";

    [Theory]
    [InlineData("shot.png", ArtifactKind.Image)]
    [InlineData("diagram.SVG", ArtifactKind.Image)]
    [InlineData("report.md", ArtifactKind.Markdown)]
    [InlineData("build.log", ArtifactKind.Text)]
    [InlineData("spec.pdf", ArtifactKind.Document)]
    [InlineData("demo.exe", ArtifactKind.Executable)]
    [InlineData("run.ps1", ArtifactKind.Executable)]
    [InlineData("bundle.zip", ArtifactKind.Archive)]
    [InlineData("something.qqq", ArtifactKind.Other)]
    [InlineData("no-extension", ArtifactKind.Other)]
    public void Classify_MapsExtensionToKind(string fileName, ArtifactKind expected)
    {
        Assert.Equal(expected, ArtifactResolver.Classify(fileName));
    }

    [Fact]
    public void Resolve_RelativePathBecomesAbsoluteAgainstProject()
    {
        var resolved = ArtifactResolver.Resolve(new ArtifactEntry(@"docs\report.md"), ProjectPath);
        Assert.Equal(Path.Combine(ProjectPath, @"docs\report.md"), resolved.AbsolutePath);
    }

    [Fact]
    public void Resolve_AbsolutePathIsKept()
    {
        var resolved = ArtifactResolver.Resolve(new ArtifactEntry(@"D:\out\demo.exe"), ProjectPath);
        Assert.Equal(@"D:\out\demo.exe", resolved.AbsolutePath);
    }

    [Fact]
    public void Resolve_LabelFallsBackToFileName()
    {
        var resolved = ArtifactResolver.Resolve(new ArtifactEntry(@"docs\report.md"), ProjectPath);
        Assert.Equal("report.md", resolved.Label);
    }

    [Fact]
    public void Resolve_ExplicitLabelWins()
    {
        var resolved = ArtifactResolver.Resolve(new ArtifactEntry(@"docs\report.md", Label: "Bug report"), ProjectPath);
        Assert.Equal("Bug report", resolved.Label);
    }

    [Fact]
    public void Resolve_MissingFileIsReportedNotDropped()
    {
        var resolved = ArtifactResolver.Resolve(new ArtifactEntry(@"gone\nothing.png"), ProjectPath);
        Assert.False(resolved.Exists);
        Assert.Equal(ArtifactKind.Image, resolved.Kind);
    }

    [Fact]
    public void Resolve_ExistingFileIsDetected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "firepit-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "note.md"), "hi");
            var resolved = ArtifactResolver.Resolve(new ArtifactEntry("note.md"), dir);
            Assert.True(resolved.Exists);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ArtifactMutatorTests
{
    private const string ProjectPath = @"C:\repos\demo";

    [Fact]
    public void Upsert_AddsNewEntry()
    {
        var (result, replaced) = ArtifactMutator.Upsert([], new ArtifactEntry("a.png"), ProjectPath);
        Assert.False(replaced);
        Assert.Single(result);
    }

    [Fact]
    public void Upsert_SameFileDifferentSeparators_ReplacesInsteadOfDuplicating()
    {
        var (first, _) = ArtifactMutator.Upsert([], new ArtifactEntry("docs/report.md", Label: "old"), ProjectPath);
        var (second, replaced) = ArtifactMutator.Upsert(first, new ArtifactEntry(@"docs\report.md", Label: "new"), ProjectPath);

        Assert.True(replaced);
        Assert.Single(second);
        Assert.Equal("new", second[0].Label);
    }

    [Fact]
    public void Upsert_RelativeAndAbsoluteToSameFileCollapse()
    {
        var absolute = Path.Combine(ProjectPath, "out", "demo.exe");
        var (first, _) = ArtifactMutator.Upsert([], new ArtifactEntry(@"out\demo.exe"), ProjectPath);
        var (second, replaced) = ArtifactMutator.Upsert(first, new ArtifactEntry(absolute), ProjectPath);

        Assert.True(replaced);
        Assert.Single(second);
    }

    [Fact]
    public void Upsert_ReplaceKeepsPosition()
    {
        IReadOnlyList<ArtifactEntry> list = [new("a.png"), new("b.png"), new("c.png")];
        var (result, _) = ArtifactMutator.Upsert(list, new ArtifactEntry("b.png", Label: "middle"), ProjectPath);

        Assert.Equal(3, result.Count);
        Assert.Equal("middle", result[1].Label);
    }

    [Fact]
    public void RemoveByPath_RemovesMatch()
    {
        IReadOnlyList<ArtifactEntry> list = [new("a.png"), new("b.png")];
        var (result, removed) = ArtifactMutator.RemoveByPath(list, "a.png", ProjectPath);

        Assert.True(removed);
        Assert.Single(result);
        Assert.Equal("b.png", result[0].Path);
    }

    [Fact]
    public void RemoveByPath_UnknownPathIsNoOp()
    {
        IReadOnlyList<ArtifactEntry> list = [new("a.png")];
        var (result, removed) = ArtifactMutator.RemoveByPath(list, "zzz.png", ProjectPath);

        Assert.False(removed);
        Assert.Single(result);
    }

    [Fact]
    public void RemoveByLabel_MatchesExplicitAndDerivedLabels()
    {
        IReadOnlyList<ArtifactEntry> list = [new("a.png", Label: "Screenshot"), new("b.png")];

        var (byExplicit, removedExplicit) = ArtifactMutator.RemoveByLabel(list, "screenshot", ProjectPath);
        Assert.True(removedExplicit);
        Assert.Single(byExplicit);

        var (byDerived, removedDerived) = ArtifactMutator.RemoveByLabel(list, "b.png", ProjectPath);
        Assert.True(removedDerived);
        Assert.Single(byDerived);
    }

    [Fact]
    public void RemoveByPath_EmptyListIsSafe()
    {
        var (result, removed) = ArtifactMutator.RemoveByPath(null, "a.png", ProjectPath);
        Assert.False(removed);
        Assert.Empty(result);
    }
}

public class JsonArtifactStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonArtifactStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "firepit-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_MissingFileReturnsEmpty()
    {
        Assert.Empty(new JsonArtifactStore().Load(_dir));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonArtifactStore();
        IReadOnlyList<ArtifactEntry> entries =
        [
            new(@"docs\report.md", Label: "Report", Note: "for the review", AddedAtUtc: "2026-08-01T10:00:00Z"),
            new(@"out\demo.exe"),
        ];

        store.Save(_dir, entries);
        var loaded = store.Load(_dir);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Report", loaded[0].Label);
        Assert.Equal("for the review", loaded[0].Note);
        Assert.Equal("2026-08-01T10:00:00Z", loaded[0].AddedAtUtc);
        Assert.Null(loaded[1].Label);
    }

    [Fact]
    public void Save_WritesIntoDotFirepit()
    {
        new JsonArtifactStore().Save(_dir, [new("a.png")]);
        Assert.True(File.Exists(Path.Combine(_dir, ".firepit", "artifacts.json")));
    }

    [Fact]
    public void Load_MalformedFileReturnsEmptyInsteadOfThrowing()
    {
        var path = Path.Combine(_dir, ".firepit", "artifacts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(new JsonArtifactStore().Load(_dir));
    }

    [Fact]
    public void NewestFirst_PutsTheMostRecentlyAddedOnTop()
    {
        ArtifactEntry[] stored = [new("first.md"), new("second.md"), new("third.md")];

        var shown = ArtifactOrder.NewestFirst(stored);

        Assert.Equal(["third.md", "second.md", "first.md"], shown.Select(e => e.Path));
    }

    [Fact]
    public void NewestFirst_OrdersEntriesThatCarryNoTimestamp()
    {
        // Hand-written entries have no AddedAtUtc at all. Position is defined
        // for them, which is the reason the order does not read the timestamp.
        ArtifactEntry[] stored =
        [
            new("old.md", AddedAtUtc: "2026-01-01T00:00:00Z"),
            new("handwritten.md"),
        ];

        var shown = ArtifactOrder.NewestFirst(stored);

        Assert.Equal("handwritten.md", shown[0].Path);
    }

    [Fact]
    public void NewestFirst_LeavesStorageAlone()
    {
        // The pane is flipped, the file is not: storage stays append-ordered
        // so it keeps reading as the history of what was pinned.
        ArtifactEntry[] stored = [new("a.md"), new("b.md")];

        _ = ArtifactOrder.NewestFirst(stored);

        Assert.Equal(["a.md", "b.md"], stored.Select(e => e.Path));
    }

    [Fact]
    public void NewestFirst_EmptyAndNullAreEmpty()
    {
        Assert.Empty(ArtifactOrder.NewestFirst<ArtifactEntry>(null));
        Assert.Empty(ArtifactOrder.NewestFirst<ArtifactEntry>([]));
    }

    [Theory]
    [InlineData(ArtifactKind.Markdown, true)]
    [InlineData(ArtifactKind.Text, true)]
    [InlineData(ArtifactKind.Image, true)]
    [InlineData(ArtifactKind.Document, false)]
    [InlineData(ArtifactKind.Executable, false)]
    [InlineData(ArtifactKind.Archive, false)]
    [InlineData(ArtifactKind.Other, false)]
    public void CanPreview_ClaimsOnlyWhatFirepitRenders(ArtifactKind kind, bool expected)
    {
        // The false cases matter more than the true ones: a PDF or an installer
        // has a real handler, and a half-built viewer would be worse than it.
        Assert.Equal(expected, ArtifactPreview.CanPreview(kind));
    }

    [Fact]
    public void Build_RendersMarkdownStructure()
    {
        var html = ArtifactPreview.Build(
            ArtifactKind.Markdown, "report.md", "# Title\n\nSome **bold** text.\n");

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("Title", html, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RendersMarkdownTables()
    {
        // Tables come from the advanced extensions; a report without them
        // renders as a wall of pipes, which is the common real-world case.
        var html = ArtifactPreview.Build(
            ArtifactKind.Markdown, "r.md", "| a | b |\n|---|---|\n| 1 | 2 |\n");

        Assert.Contains("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesPlainTextInsteadOfRenderingIt()
    {
        // A pinned log is not markup. Left unescaped, a line of HTML in a build
        // log would be rendered as HTML — at best mangling the log, at worst
        // acting on content Firepit did not write.
        var html = ArtifactPreview.Build(
            ArtifactKind.Text, "build.log", "<script>alert(1)</script> & <b>x</b>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PointsAnImageAtTheVirtualHostNotTheFileSystem()
    {
        // WebView2 refuses file://, so the image is reached through the mapped
        // host — the same rule the terminal's assets live by.
        var html = ArtifactPreview.Build(ArtifactKind.Image, "shot 1.png", null);

        Assert.Contains($"<base href=\"https://{ArtifactPreview.VirtualHost}/\">", html, StringComparison.Ordinal);
        Assert.Contains("shot%201.png", html, StringComparison.Ordinal);
        Assert.DoesNotContain("file://", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TruncatesHugeTextAndSaysSo()
    {
        var huge = new string('x', ArtifactPreview.MaxTextChars + 500);

        var html = ArtifactPreview.Build(ArtifactKind.Text, "huge.log", huge);

        Assert.Contains("Truncated", html, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', ArtifactPreview.MaxTextChars + 1), html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesTheFileNameInTheTitle()
    {
        var html = ArtifactPreview.Build(ArtifactKind.Text, "<evil>.txt", "hi");

        Assert.Contains("&lt;evil&gt;.txt", html, StringComparison.Ordinal);
    }
}
