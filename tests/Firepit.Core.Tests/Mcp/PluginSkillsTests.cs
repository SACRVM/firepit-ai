using System.IO;
using System.Text.RegularExpressions;
using Firepit.Core.Mcp;

namespace Firepit.Core.Tests.Mcp;

/// <summary>
/// The firepit plugin's skills are agent-facing text like the server
/// instructions: every session that loads one follows it. A tool they name
/// that does not exist, or a skill the instructions point at that is not
/// there, teaches the wrong thing, and nothing else in the build notices.
/// </summary>
public partial class PluginSkillsTests
{
    private static readonly string Repo = FindRepo();
    private static readonly string SkillsDir = Path.Combine(Repo, "mods", "firepit", "skills");

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Firepit.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Firepit.slnx not found above the test binaries");
    }

    private static IReadOnlySet<string> Tools()
    {
        var catalogue = File.ReadAllText(Path.Combine(Repo, "src", "Firepit.Mcp", "McpToolDefinitions.cs"));
        return ToolDefinition().Matches(catalogue).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static string[] SkillNames() =>
        Directory.GetDirectories(SkillsDir).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToArray();

    public static TheoryData<string> Skills() => new(SkillNames());

    private static string Read(string skill) => File.ReadAllText(Path.Combine(SkillsDir, skill, "SKILL.md"));

    [Fact]
    public void TheFourSkillsExist()
    {
        Assert.Equal(["commands", "knowledge", "messages", "setup"], SkillNames());
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void ASkill_IsNamedAfterItsFolder_AndSaysWhenToUseIt(string skill)
    {
        var match = Frontmatter().Match(Read(skill));

        Assert.True(match.Success, $"{skill}: no frontmatter with name and description");
        Assert.Equal(skill, match.Groups["name"].Value);
        var description = match.Groups["description"].Value;
        Assert.Contains("Use when", description, StringComparison.Ordinal);
        Assert.InRange(description.Length, 100, 1024);
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryToolASkillNames_Exists(string skill)
    {
        var tools = Tools();
        var named = ToolName().Matches(Read(skill)).Select(m => m.Value).Distinct();

        Assert.All(named, tool => Assert.True(tools.Contains(tool), $"{skill} names {tool}, which is no Firepit tool"));
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void ASkill_IsEnglish(string skill)
    {
        var text = Read(skill);

        Assert.DoesNotContain(text, c => "äöüßÄÖÜ".Contains(c));
        foreach (var word in new[] { "nicht", "Datei", "Ordner", "und", "oder", "wird", "kann" })
        {
            Assert.False(Regex.IsMatch(text, $@"\b{word}\b", RegexOptions.IgnoreCase), $"{skill} contains German: '{word}'");
        }
    }

    [Fact]
    public void EverySkillTheInstructionsPointAt_Exists()
    {
        var pointed = SkillReference().Matches(FirepitServerInstructions.Text).Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(pointed);
        Assert.All(pointed, skill => Assert.True(File.Exists(Path.Combine(SkillsDir, skill, "SKILL.md")), $"firepit:{skill} does not exist"));
    }

    [GeneratedRegex("""new\("(firepit_[a-z_]+)",""")]
    private static partial Regex ToolDefinition();

    [GeneratedRegex(@"\bfirepit_[a-z]+(?:_[a-z]+)*\b")]
    private static partial Regex ToolName();

    [GeneratedRegex("""\A---\nname: (?<name>[a-z-]+)\ndescription: "(?<description>[^"\n]+)"\n---\n""")]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"\bfirepit:([a-z-]+)")]
    private static partial Regex SkillReference();
}
