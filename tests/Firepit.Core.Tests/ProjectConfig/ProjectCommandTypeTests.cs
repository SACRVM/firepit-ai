using System.IO;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.ProjectConfig;

public class ProjectCommandTypeTests : IDisposable
{
    private readonly string _project;
    private readonly JsonProjectConfigStore _store = new();

    public ProjectCommandTypeTests()
    {
        _project = Path.Combine(Path.GetTempPath(), "firepit-type-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_project, ".firepit"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => JsonProjectConfigStore.ResolvePath(_project);

    // voxcade's shape: comments, blank lines, trailing commas, a claude-prompt.
    private const string Jsonc = """
        {
          // Firepit project config (JSONC).
          "version": 1,
          "id": "demo",
          "commands": [
            { "group": "Run", "name": "arena", "type": "shell", "command": "cargo", "args": ["run"], },
            { "group": "Claude", "name": "review", "type": "claude-prompt", "prompt": "Review it." },
            { "group": "Open", "name": "repo", "type": "url", "url": "https://example.com" },
          ],
        }
        """;

    [Theory]
    [InlineData("shell", ProjectCommandType.Shell)]
    [InlineData("Shell", ProjectCommandType.Shell)]
    [InlineData("claude-prompt", ProjectCommandType.ClaudePrompt)]
    [InlineData("ClaudePrompt", ProjectCommandType.ClaudePrompt)]
    [InlineData("url", ProjectCommandType.Url)]
    [InlineData("URL", ProjectCommandType.Url)]
    public void EverySpellingOfAType_Reads(string spelling, ProjectCommandType expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "version": 1, "commands": [ { "name": "x", "type": "{{spelling}}" } ] }""");

        Assert.Equal(expected, Assert.Single(_store.Load(_project)!.Commands!).Type);
    }

    [Fact]
    public void ACommentedConfigWithAClaudePrompt_ReadsWhole()
    {
        File.WriteAllText(ConfigPath, Jsonc);

        var config = _store.Load(_project);

        Assert.NotNull(config);
        Assert.Equal("demo", config.Id);
        Assert.Equal(["shell", "claude-prompt", "url"], config.Commands!.Select(c => ProjectCommandTypeConverter.Name(c.Type)));
    }

    [Fact]
    public void ASavedConfig_SpellsTypesTheDocumentedWay_AndEndsWithANewline()
    {
        File.WriteAllText(ConfigPath, Jsonc);
        _store.Save(_project, _store.Load(_project)!);

        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("\"type\": \"claude-prompt\"", text);
        Assert.Contains("\"type\": \"shell\"", text);
        Assert.DoesNotContain("\"Shell\"", text);
        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public void AFileThatDoesNotRead_IsUnreadable_NotMissing()
    {
        File.WriteAllText(ConfigPath, """{ "commands": [ { "name": "x", "type": "teleport" } ] }""");

        var read = _store.Read(_project);

        Assert.True(read.IsUnreadable);
        Assert.Contains("teleport", read.Error);
        Assert.False(new JsonProjectConfigStore().Read(Path.Combine(_project, "none")).Exists);
    }
}
