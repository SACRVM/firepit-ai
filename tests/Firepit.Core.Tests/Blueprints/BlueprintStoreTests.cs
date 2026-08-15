using Firepit.Core.Blueprints;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.Blueprints;

public sealed class BlueprintStoreTests : IDisposable
{
    private readonly string _root;

    public BlueprintStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-blueprint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".firepit"));
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
    public void EnsureDefaults_SeedsFirepitBlueprintOnce()
    {
        var store = new BlueprintStore(_root);

        Assert.True(store.EnsureDefaults());
        Assert.False(store.EnsureDefaults());

        var dir = Path.Combine(_root, ".firepit", "blueprints", "firepit");
        Assert.True(File.Exists(Path.Combine(dir, "blueprint.json")));
        Assert.True(File.Exists(Path.Combine(dir, "files", ".firepit", "knowledge", "README.md")));
        Assert.True(File.Exists(Path.Combine(dir, "files", ".firepit", "knowledge-pinned.md")));
    }

    [Fact]
    public void EnsureDefaults_WithoutMetaProject_DoesNothing()
    {
        Directory.Delete(Path.Combine(_root, ".firepit"), recursive: true);
        var store = new BlueprintStore(_root);

        Assert.False(store.EnsureDefaults());
        Assert.False(Directory.Exists(Path.Combine(_root, ".firepit")));
    }

    [Fact]
    public void LoadAll_ReadsTheSeededBlueprint()
    {
        var store = new BlueprintStore(_root);
        store.EnsureDefaults();

        var blueprint = Assert.Single(store.LoadAll());

        Assert.Equal("firepit", blueprint.Name);
        Assert.True(blueprint.EnsureProjectConfig);
        Assert.Equal(ProjectScaffolding.GitignoreEntries, blueprint.GitignoreLines);
        Assert.Equal(4, blueprint.ClaudeMdSections.Count);
        Assert.Equal(2, blueprint.Files.Count);
        var relPaths = blueprint.Files.Select(f => f.RelativePath).ToArray();
        Assert.Contains(".firepit/knowledge/README.md", relPaths);
        Assert.Contains(".firepit/knowledge-pinned.md", relPaths);
    }

    [Fact]
    public void EnsureDefaults_AtCurrentVersion_LeavesAnEditedManifestByteIdentical()
    {
        var store = new BlueprintStore(_root);
        store.EnsureDefaults();
        var manifestPath = Path.Combine(_root, ".firepit", "blueprints", "firepit", "blueprint.json");
        var edited = $$"""
            { "version": {{BlueprintStore.CurrentManifestVersion}}, "description": "my edit", "ensureProjectConfig": false }
            """;
        File.WriteAllText(manifestPath, edited);

        store.EnsureDefaults();

        // A manifest already at the current version is the user's alone —
        // including the sections they deleted. Nothing is added back.
        Assert.Equal(edited, File.ReadAllText(manifestPath));
        var blueprint = store.TryLoad("firepit");
        Assert.NotNull(blueprint);
        Assert.Equal("my edit", blueprint.Description);
        Assert.False(blueprint.EnsureProjectConfig);
        Assert.Empty(blueprint.ClaudeMdSections);
    }

    [Fact]
    public void EnsureDefaults_OnAnOlderManifest_AddsNewSectionsButKeepsEdits()
    {
        var store = new BlueprintStore(_root);
        store.EnsureDefaults();
        var manifestPath = Path.Combine(_root, ".firepit", "blueprints", "firepit", "blueprint.json");
        // A v1 manifest as 0.12.x seeded it: inbox only, plus a user edit.
        File.WriteAllText(manifestPath, """
            {
              "version": 1,
              "description": "my edit",
              "ensureProjectConfig": false,
              "claudeMd": [ { "marker": "firepit_inbox_complete", "content": "my inbox wording\n" } ]
            }
            """);

        Assert.True(store.EnsureDefaults());

        var blueprint = store.TryLoad("firepit");
        Assert.NotNull(blueprint);
        // The user's own fields and their reworded section survive verbatim.
        Assert.Equal("my edit", blueprint.Description);
        Assert.False(blueprint.EnsureProjectConfig);
        var inbox = Assert.Single(
            blueprint.ClaudeMdSections, s => s.Marker == "firepit_inbox_complete");
        Assert.Equal("my inbox wording\n", inbox.Content);
        // Sections introduced after v1 are appended — this is the whole point:
        // a new convention has to reach meta projects that already exist.
        Assert.Contains(
            blueprint.ClaudeMdSections,
            s => s.Marker == FirepitBlueprintDefaults.ArtifactsSectionMarker);

        // Migration runs exactly once.
        Assert.False(store.EnsureDefaults());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("name.with.dots")]
    [InlineData("")]
    public void TryLoad_RejectsPathLikeNames(string name)
    {
        var store = new BlueprintStore(_root);
        store.EnsureDefaults();

        Assert.Null(store.TryLoad(name));
    }
}
