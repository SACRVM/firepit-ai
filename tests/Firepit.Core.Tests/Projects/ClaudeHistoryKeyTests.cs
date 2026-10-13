using Firepit.Core.Projects;

namespace Firepit.Core.Tests.Projects;

public sealed class ClaudeHistoryKeyTests
{
    [Theory]
    [InlineData(@"D:\repos\firepit-ai", "D--repos-firepit-ai")]
    [InlineData(@"D:\repos\.firepit", "D--repos--firepit")]
    [InlineData(@"D:\repos\my_proj", "D--repos-my-proj")]
    [InlineData(@"\\nas\music", "--nas-music")]
    [InlineData(@"C:\Users\jo do\stuff", "C--Users-jo-do-stuff")]
    public void Encode_ReplacesEveryNonAlphanumericWithDash(string path, string expected)
    {
        Assert.Equal(expected, ClaudeHistoryKey.Encode(path));
    }

    [WindowsFact]
    public void GetHistoryDir_ComposesUnderDotClaudeProjects()
    {
        var dir = ClaudeHistoryKey.GetHistoryDir(@"C:\Users\jane", @"D:\repos\firepit-ai");

        Assert.Equal(@"C:\Users\jane\.claude\projects\D--repos-firepit-ai", dir);
    }

    [WindowsFact]
    public void GetHistoryDir_NormalisesRelativeSegments()
    {
        var dir = ClaudeHistoryKey.GetHistoryDir(@"C:\Users\jane", @"D:\repos\sub\..\firepit-ai");

        Assert.EndsWith(@"\D--repos-firepit-ai", dir, StringComparison.Ordinal);
    }

    [UnixFact]
    public void GetHistoryDir_OnUnix_MatchesClaudeCodesKey()
    {
        var dir = ClaudeHistoryKey.GetHistoryDir("/home/jane", "/home/jane/repos/sub/../firepit-ai");

        Assert.Equal("/home/jane/.claude/projects/-home-jane-repos-firepit-ai", dir);
    }
}
