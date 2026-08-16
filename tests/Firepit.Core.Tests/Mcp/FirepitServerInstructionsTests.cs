using System.Text.RegularExpressions;
using Firepit.Core.Blueprints;
using Firepit.Core.Mcp;

namespace Firepit.Core.Tests.Mcp;

public class FirepitServerInstructionsTests
{
    /// <summary>
    /// Every agent-facing block of prose Firepit ships. The MCP instructions
    /// reach the agent at handshake, the blueprint sections reach it through
    /// CLAUDE.md — both are product surface and both must hold the same line.
    /// </summary>
    public static TheoryData<string, string> AgentFacingText() => new()
    {
        { nameof(FirepitServerInstructions), FirepitServerInstructions.Text },
        { nameof(FirepitBlueprintDefaults.InboxSection),     FirepitBlueprintDefaults.InboxSection },
        { nameof(FirepitBlueprintDefaults.ArtifactsSection), FirepitBlueprintDefaults.ArtifactsSection },
        { nameof(FirepitBlueprintDefaults.KnowledgeSection), FirepitBlueprintDefaults.KnowledgeSection },
        { nameof(FirepitBlueprintDefaults.PinnedSection),    FirepitBlueprintDefaults.PinnedSection },
        { nameof(FirepitBlueprintDefaults.KnowledgeReadme),  FirepitBlueprintDefaults.KnowledgeReadme },
    };

    // The app is English, every string — including the prose we hand to agents.
    // Sessions are usually held in German and this has regressed twice, so it
    // gets a test rather than a habit.
    [Theory]
    [MemberData(nameof(AgentFacingText))]
    public void AgentFacingText_IsEnglish(string name, string text)
    {
        Assert.DoesNotContain(text, c => "äöüßÄÖÜ".Contains(c));

        // Whole words only — "background" is not "und", "wander" is not "and".
        foreach (var word in new[] { "nicht", "Datei", "Ordner", "und", "oder", "wird", "kann" })
        {
            Assert.False(
                Regex.IsMatch(text, $@"\b{word}\b", RegexOptions.IgnoreCase),
                $"{name} contains German: '{word}'");
        }
    }

    // Unlike the CLAUDE.md sections, this text is loaded into the context of
    // every session in every project. A budget is the only thing standing
    // between "our conventions" and "our manual".
    [Fact]
    public void Text_StaysWithinItsContextBudget()
    {
        Assert.InRange(FirepitServerInstructions.Text.Length, 500, 4000);
    }

    // The instructions teach habits — pin as you go, close what you read,
    // search before researching. A habit with a misspelled tool name teaches
    // nothing, and nothing else in the build would catch the typo.
    [Theory]
    [InlineData("firepit_artifact_add")]
    [InlineData("firepit_artifact_list")]
    [InlineData("firepit_artifact_remove")]
    [InlineData("firepit_inbox_list")]
    [InlineData("firepit_inbox_complete")]
    [InlineData("firepit_knowledge_search")]
    [InlineData("firepit_knowledge_add")]
    [InlineData("firepit_list_projects")]
    [InlineData("firepit_send_to")]
    public void Text_NamesTheToolItTeaches(string tool)
    {
        Assert.Contains(tool, FirepitServerInstructions.Text, StringComparison.Ordinal);
    }
}
