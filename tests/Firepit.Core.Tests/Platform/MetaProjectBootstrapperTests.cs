using System.IO;
using System.Text.Json.Nodes;
using Firepit.Core.Blueprints;
using Firepit.Core.Platform;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.Platform;

public class MetaProjectBootstrapperTests : IDisposable
{
    private readonly string _projectsRoot;
    private readonly MetaProjectBootstrapper _bootstrapper = new();

    public MetaProjectBootstrapperTests()
    {
        _projectsRoot = Path.Combine(Path.GetTempPath(), "firepit-meta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectsRoot, recursive: true); } catch { /* best effort */ }
    }

    private string Meta => _bootstrapper.GetMetaProjectPath(_projectsRoot);

    [Fact]
    public void Exists_FalseBeforeBootstrap()
    {
        Assert.False(_bootstrapper.Exists(_projectsRoot));
    }

    [Fact]
    public void Exists_FalseForAFolderHoldingOnlyAnIndex()
    {
        // What a daemon that ran before the central project existed leaves
        // behind: the global base's index, and nothing discovery would count.
        Directory.CreateDirectory(Meta);
        File.WriteAllText(Path.Combine(Meta, "knowledge.db"), "");

        Assert.False(_bootstrapper.Exists(_projectsRoot));
    }

    [Fact]
    public void Bootstrap_CreatesExpectedFiles()
    {
        var written = _bootstrapper.Bootstrap(_projectsRoot);
        Assert.True(_bootstrapper.Exists(_projectsRoot));

        string[] expected =
        [
            "CLAUDE.md",
            "README.md",
            ".gitignore",
            ".mcp.json",
            ".firepit/config.json",
            "knowledge/README.md",
            "knowledge-pinned.md",
            ".firepit/knowledge/README.md",
            ".firepit/knowledge-pinned.md",
            "projects/claude.md",
            "projects/claude-github-public.md",
            "projects/claude-github-private.md",
        ];
        foreach (var relative in expected)
        {
            Assert.True(File.Exists(Path.Combine(Meta, relative)), $"{relative} was not seeded");
        }

        Assert.Equal(
            expected.Select(r => Path.Combine(Meta, r)).Order(StringComparer.Ordinal),
            written.Order(StringComparer.Ordinal));

        Assert.False(Directory.Exists(Path.Combine(Meta, "inbox")),
            "root-level inbox/ was retired in favour of .firepit/inbox/ — bootstrapper must not recreate it.");
    }

    [Fact]
    public void Bootstrap_RegistersFirepitInMcpJson_WhereClaudeCodeReadsIt()
    {
        _bootstrapper.Bootstrap(_projectsRoot);

        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(Meta, ".mcp.json")))!;
        Assert.Equal("stdio", (string?)mcp["mcpServers"]?["firepit"]?["type"]);
        Assert.Equal("firepit-mcp", (string?)mcp["mcpServers"]?["firepit"]?["command"]);
        Assert.False(File.Exists(Path.Combine(Meta, ".claude", "settings.json")),
            "Claude Code does not read MCP servers from .claude/settings.json.");
    }

    [Fact]
    public void Bootstrap_ConfigIsTheProjectScaffold_WithTheCentralId()
    {
        _bootstrapper.Bootstrap(_projectsRoot);

        var config = new JsonProjectConfigStore().Load(Meta);
        Assert.NotNull(config);
        Assert.Equal("firepit-central", config.Id);
        Assert.Null(config.McpActivations);
    }

    [Fact]
    public void Bootstrap_LeavesTheCentralProjectBlueprintConformant()
    {
        _bootstrapper.Bootstrap(_projectsRoot);
        var store = new BlueprintStore(_projectsRoot);
        store.EnsureDefaults();

        var check = BlueprintApplier.Check(store.TryLoad("firepit")!, Meta);

        Assert.True(check.Conformant, string.Join("; ", check.DescribePending()));
    }

    [Fact]
    public void Bootstrap_ClaudeMdImportsTheSharedFragment_AndBothPinnedDigests()
    {
        _bootstrapper.Bootstrap(_projectsRoot);

        var claudeMd = File.ReadAllText(Path.Combine(Meta, "CLAUDE.md"));
        Assert.Contains("@projects/claude.md\n", claudeMd, StringComparison.Ordinal);
        Assert.Contains("@knowledge-pinned.md\n", claudeMd, StringComparison.Ordinal);
        Assert.Contains("@.firepit/knowledge-pinned.md\n", claudeMd, StringComparison.Ordinal);
        Assert.Contains(FirepitBlueprintDefaults.ArtifactsSection, claudeMd, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", claudeMd, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_DescribesTheProductAsItIs()
    {
        _bootstrapper.Bootstrap(_projectsRoot);

        // The WPF shell's vocabulary: tabs, the toolbar's quick links, MCP
        // activations, the paperclip pane, and a Fishbowl recipe for Windows.
        string[] retired =
        [
            "firepit_open_tab", "firepit_focus_tab", "firepit_close_tab", "firepit://sessions",
            "quickLinks", "mcpActivations", "paperclip", "Fishbowl", "FISHBOWL", "cmdkey",
            "PTY", "toolbar", ".claude/settings.json",
        ];
        foreach (var file in Directory.EnumerateFiles(Meta, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var word in retired)
            {
                Assert.False(text.Contains(word, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(Meta, file)} still mentions {word}");
            }
        }
    }

    [Fact]
    public void Bootstrap_GitignoreCarriesEveryProjectEntry()
    {
        _bootstrapper.Bootstrap(_projectsRoot);

        Assert.Empty(ProjectScaffolding.GetMissingGitignoreEntries(Meta, ProjectScaffolding.GitignoreEntries));
        var gitignore = File.ReadAllText(Path.Combine(Meta, ".gitignore"));
        Assert.Contains("/knowledge.db*\n", gitignore, StringComparison.Ordinal);
        Assert.Contains("projects/*/knowledge.db*\n", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrap_LeavesAPreSplitGlobalBaseWhereItIs()
    {
        // Before the split the global base lived in .firepit/knowledge/. A
        // knowledge/ next to it would turn those documents into local notes.
        var legacy = Path.Combine(Meta, ".firepit", "knowledge");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "finding.md"), "# Finding\n");

        _bootstrapper.Bootstrap(_projectsRoot);

        Assert.False(Directory.Exists(Path.Combine(Meta, "knowledge")));
        Assert.False(File.Exists(Path.Combine(legacy, "README.md")));
    }

    [Fact]
    public void Bootstrap_CleansUpEmptyLegacyRootInbox()
    {
        var legacyInbox = Path.Combine(Meta, "inbox");
        Directory.CreateDirectory(legacyInbox);
        File.WriteAllText(Path.Combine(legacyInbox, ".gitkeep"), "");

        _bootstrapper.Bootstrap(_projectsRoot);

        Assert.False(Directory.Exists(legacyInbox),
            "an empty legacy root inbox/ should be removed on bootstrap to clear post-upgrade drift.");
    }

    [Fact]
    public void Bootstrap_PreservesNonEmptyLegacyRootInbox()
    {
        var legacyInbox = Path.Combine(Meta, "inbox");
        Directory.CreateDirectory(legacyInbox);
        File.WriteAllText(Path.Combine(legacyInbox, "user-note.md"), "important");

        _bootstrapper.Bootstrap(_projectsRoot);

        Assert.True(Directory.Exists(legacyInbox),
            "a legacy root inbox/ with user-curated content must not be deleted.");
        Assert.True(File.Exists(Path.Combine(legacyInbox, "user-note.md")));
    }

    [Fact]
    public void Bootstrap_DoesNotClobberExistingFiles()
    {
        Directory.CreateDirectory(Path.Combine(Meta, "projects"));
        File.WriteAllText(Path.Combine(Meta, "CLAUDE.md"), "MY CUSTOM CONTENT");
        File.WriteAllText(Path.Combine(Meta, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(Meta, "projects", "claude.md"), "MY POLICY");

        var written = _bootstrapper.Bootstrap(_projectsRoot);

        Assert.Equal("MY CUSTOM CONTENT", File.ReadAllText(Path.Combine(Meta, "CLAUDE.md")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Meta, ".mcp.json")));
        Assert.Equal("MY POLICY", File.ReadAllText(Path.Combine(Meta, "projects", "claude.md")));
        // Other files still wrote
        Assert.Contains(written, p => p.EndsWith("README.md"));
        Assert.DoesNotContain(written, p => p.EndsWith("CLAUDE.md"));
        Assert.DoesNotContain(written, p => p.EndsWith(".mcp.json"));
        Assert.DoesNotContain(written, p => p.EndsWith("claude.md"));
    }

    [Fact]
    public void Bootstrap_IsIdempotent()
    {
        _bootstrapper.Bootstrap(_projectsRoot);
        var second = _bootstrapper.Bootstrap(_projectsRoot);
        // Second run writes nothing — every file already exists.
        Assert.Empty(second);
    }

    [Fact]
    public void GetMetaProjectPath_PointsAtDotFirepitUnderRoot()
    {
        Assert.Equal(
            Path.Combine(_projectsRoot, ".firepit"),
            _bootstrapper.GetMetaProjectPath(_projectsRoot));
    }
}
