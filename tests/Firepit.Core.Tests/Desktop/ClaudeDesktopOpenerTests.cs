using System.IO;
using Firepit.Core.Desktop;

namespace Firepit.Core.Tests.Desktop;

public class ClaudeDesktopOpenerTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessions;
    private readonly List<string> _launched = [];

    public ClaudeDesktopOpenerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-desktop-tests", Guid.NewGuid().ToString("N"));
        // The app keeps one folder per account and organisation.
        _sessions = Path.Combine(_root, "claude-code-sessions");
        Directory.CreateDirectory(Path.Combine(_sessions, "account", "org"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Session(string id, string cwd, long lastActivityAt, bool archived = false) =>
        File.WriteAllText(Path.Combine(_sessions, "account", "org", $"{id}.json"), $$"""
            { "sessionId": "{{id}}", "cliSessionId": "x", "cwd": "{{cwd}}", "originCwd": "{{cwd}}",
              "isArchived": {{(archived ? "true" : "false")}}, "lastActivityAt": {{lastActivityAt}}, "title": "t" }
            """);

    private ClaudeDesktopOpener Opener(bool launches = true) =>
        new(_sessions, url => { _launched.Add(url); return launches; });

    [UnixFact]
    public void AProjectWithSessions_OpensTheMostRecentOneInItsOwnFolder()
    {
        Session("local_old", "/r/vectorizer", 100);
        Session("local_new", "/r/vectorizer/", 300);
        Session("local_archived", "/r/vectorizer", 900, archived: true);
        Session("local_worktree", "/r/vectorizer/.claude/worktrees/x", 950);
        Session("local_other", "/r/vectorizer-two", 999);

        var outcome = Opener().Open("vectorizer", "/r/vectorizer");

        Assert.True(outcome.Ok);
        Assert.Equal(["claude://claude.ai/epitaxy/local_new"], _launched);
        Assert.Contains("Opened vectorizer", outcome.Message);
    }

    [UnixFact]
    public void AProjectWithoutASession_GetsANewOneInItsFolder()
    {
        Session("local_other", "/r/other", 1);

        var outcome = Opener().Open("my project", "/r/my project");

        Assert.True(outcome.Ok);
        Assert.Equal(["claude://code/new?folder=%2Fr%2Fmy%20project"], _launched);
        Assert.Contains("new Claude Code session", outcome.Message);
    }

    [Fact]
    public void UnreadableRecords_AreSkipped_AndAMissingFolderMeansNoSessions()
    {
        File.WriteAllText(Path.Combine(_sessions, "account", "org", "local_broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_sessions, "account", "org", "local_noid.json"), """{ "cwd": "/r/a" }""");
        Session("local_ok", "/r/a", 5);

        Assert.Equal(["local_ok"], ClaudeDesktopOpener.ReadSessions(_sessions).Select(s => s.Id));
        Assert.Empty(ClaudeDesktopOpener.ReadSessions(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void AFailedHandOver_SaysWhatItTried()
    {
        var outcome = Opener(launches: false).Open("a", Path.Combine(_root, "a"));

        Assert.False(outcome.Ok);
        Assert.Contains("claude://code/new?folder=", outcome.Message);
    }
}
