using Firepit.Core.Settings;
using Firepit.Core.State;
using Firepit.Mcp;

namespace Firepit.Workspace.Tests;

/// <summary>
/// The workspace as the daemon runs it: no shell, no knowledge model, a
/// projects root of its own under the temp directory.
/// </summary>
public sealed class WorkspaceTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _projects;
    private readonly JsonSettingsStore _settingsStore;
    private FirepitWorkspace _ws = null!;

    public WorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-workspace-tests", Guid.NewGuid().ToString("N"));
        _projects = Path.Combine(_root, "repos");
        MakeProject("alpha");
        MakeProject("alpha-two");
        MakeProject("beta");
        Directory.CreateDirectory(Path.Combine(_projects, "not-a-project"));
        _settingsStore = new JsonSettingsStore(Path.Combine(_root, "config", "settings.json"));
        _settingsStore.Save(FirepitSettings.Defaults with { ProjectsRoot = _projects });
    }

    public async Task InitializeAsync()
    {
        _ws = new FirepitWorkspace(
            new HeadlessTabHost(),
            _settingsStore,
            new JsonStateStore(Path.Combine(_root, "state", "state.json")),
            runJobs: false,
            knowledgeDataRoot: "");
        await _ws.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _ws.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeProject(string name)
    {
        var dir = Path.Combine(_projects, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), $"# {name}\n");
        return dir;
    }

    [Fact]
    public async Task Projects_AreTheQualifiedFolders()
    {
        var projects = await _ws.ListProjectsAsync();

        Assert.Equal(["alpha", "alpha-two", "beta"], projects.Select(p => p.Name).Order());
        Assert.All(projects, p => Assert.False(p.IsOpen));
    }

    [Fact]
    public void AWorkingDirectory_BelongsToTheClosestProjectAboveIt()
    {
        var deep = Path.Combine(_projects, "alpha", "src", "deep");
        Directory.CreateDirectory(deep);

        Assert.Equal("alpha", _ws.FindProjectByPath(deep)?.Name);
        Assert.Equal("alpha", _ws.FindProjectByPath(Path.Combine(_projects, "alpha") + "/")?.Name);
        // A sibling whose name starts the same is not inside it.
        Assert.Equal("alpha-two", _ws.FindProjectByPath(Path.Combine(_projects, "alpha-two"))?.Name);
        Assert.Null(_ws.FindProjectByPath(Path.Combine(_projects, "not-a-project")));
        Assert.Null(_ws.FindProjectByPath(""));
    }

    [Fact]
    public async Task AMessage_GoesIn_IsListed_AndIsCompleted()
    {
        var sent = await _ws.SendInboxAsync("alpha", "beta", "Hello there", "Body text.", "normal");
        Assert.True(sent.Ok, sent.Message);

        var inbox = await _ws.ListInboxAsync("beta");
        var message = Assert.Single(inbox.Messages);
        Assert.Equal("alpha", message.From);
        Assert.Equal("Hello there", message.Subject);
        Assert.Contains("Body text.", message.Body);

        var done = await _ws.CompleteInboxAsync("beta", message.Id);
        Assert.True(done.Ok, done.Message);
        Assert.Empty((await _ws.ListInboxAsync("beta")).Messages);
        Assert.True(File.Exists(Path.Combine(_projects, "beta", ".firepit", "inbox", "processed", message.Id)));
    }

    [Fact]
    public async Task AnInboxId_CannotClimbOutOfTheInbox()
    {
        var result = await _ws.CompleteInboxAsync("beta", "../CLAUDE.md");

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(_projects, "beta", "CLAUDE.md")));
    }

    [Fact]
    public async Task ACommand_IsAdded_Listed_AndRemoved()
    {
        var added = await _ws.AddProjectCommandAsync("alpha", new AddCommandSpec(
            Name: "status", Type: "shell", Command: "git", Args: ["status"], Group: "Git"));
        Assert.True(added.Ok, added.Message);

        var listed = await _ws.ListProjectCommandsAsync("alpha");
        var command = Assert.Single(listed.Commands);
        Assert.Equal(("status", "shell", "git", "Git"), (command.Name, command.Type, command.Command, command.Group));

        var removed = await _ws.RemoveProjectCommandAsync("alpha", "status");
        Assert.True(removed.Ok, removed.Message);
        Assert.Empty((await _ws.ListProjectCommandsAsync("alpha")).Commands);
    }

    [Fact]
    public async Task ChangingOneCommand_KeepsTheOthersDescriptions()
    {
        // The commands mod reads description and bar; a rewrite of the file
        // through the tools must carry them, not drop what it doesn't know.
        var config = Path.Combine(_projects, "alpha", ".firepit", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, """
            { "version": 1, "id": "alpha", "commands": [
              { "group": "Run", "name": "site", "type": "shell", "command": "python3",
                "description": "Serves the site.", "bar": false } ] }
            """);

        var added = await _ws.AddProjectCommandAsync("alpha", new AddCommandSpec(
            Name: "status", Type: "shell", Command: "git", Group: "Git",
            Description: "Shows the branch.", KeepOpenOnError: true));
        Assert.True(added.Ok, added.Message);

        var commands = (await _ws.ListProjectCommandsAsync("alpha")).Commands;
        Assert.Equal(
            [("site", "Serves the site.", (bool?)false), ("status", "Shows the branch.", (bool?)null)],
            commands.Select(c => (c.Name, c.Description, c.Bar)));
        Assert.True(commands[1].KeepOpenOnError);
        Assert.Contains("\"description\": \"Serves the site.\"", await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task ANameTwoGroupsShare_IsRemovedOnlyWithItsGroup()
    {
        await _ws.AddProjectCommandAsync("alpha", new AddCommandSpec(Name: "debug", Type: "shell", Command: "a", Group: "Build"));
        await _ws.AddProjectCommandAsync("alpha", new AddCommandSpec(Name: "debug", Type: "shell", Command: "b", Group: "Run"));

        var vague = await _ws.RemoveProjectCommandAsync("alpha", "debug");
        Assert.False(vague.Ok);
        Assert.Contains("Build debug", vague.Message);

        var exact = await _ws.RemoveProjectCommandAsync("alpha", "Run debug");
        Assert.True(exact.Ok, exact.Message);
        Assert.Equal("Build", Assert.Single((await _ws.ListProjectCommandsAsync("alpha")).Commands).Group);
    }

    [Fact]
    public async Task AnArtifact_IsPinned_Listed_AndUnpinned()
    {
        var file = Path.Combine(_projects, "alpha", "report.md");
        await File.WriteAllTextAsync(file, "# Report\n");

        var added = await _ws.AddArtifactAsync("alpha", "report.md", "The report", null);
        Assert.True(added.Ok, added.Message);

        var artifact = Assert.Single((await _ws.ListArtifactsAsync("alpha")).Artifacts);
        Assert.Equal(("The report", "markdown", true), (artifact.Label, artifact.Kind, artifact.Exists));

        var removed = await _ws.RemoveArtifactAsync("alpha", null, "The report");
        Assert.True(removed.Ok, removed.Message);
        Assert.Empty((await _ws.ListArtifactsAsync("alpha")).Artifacts);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task WithoutAShell_TheTabToolsSaySo_AndReloadStillWorks()
    {
        var open = await _ws.OpenTabAsync("alpha", resume: false);
        Assert.False(open.Ok);
        Assert.Contains("No Firepit shell", open.Message);

        Assert.Empty(await _ws.ListSessionsAsync());

        var reload = await _ws.ReloadAsync("alpha", restart: false);
        Assert.True(reload.Ok, reload.Message);
    }

    [Fact]
    public async Task WithoutTheKnowledgeService_SearchSaysItIsNotRunning()
    {
        var result = await _ws.SearchKnowledgeAsync("alpha", "both", "anything", 5);

        Assert.False(result.Ok);
        Assert.Contains("not running", result.Message);
    }

    [Fact]
    public async Task AnUnknownProject_IsNamedInTheAnswer()
    {
        var inbox = await _ws.ListInboxAsync("gamma");

        Assert.Empty(inbox.Messages);
        Assert.Contains("gamma", inbox.Error);
    }

    [Fact]
    public async Task ProjectsCreatedAtTheSameTime_AreAllRegistered()
    {
        // Each create reads the settings, adds an entry and writes them back.
        // The window serialised that on its UI thread; without the gate,
        // parallel creates would overwrite each other's entries.
        var names = Enumerable.Range(1, 12).Select(i => $"made-{i}").ToArray();

        var results = await Task.WhenAll(names.Select(n =>
            Task.Run(() => _ws.CreateProjectAsync(n, path: null, applyBlueprint: false))));

        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        var registered = _settingsStore.Load().Projects!.Select(p => p.Name).ToHashSet();
        Assert.Superset(names.ToHashSet(), registered);
    }

    [Fact]
    public async Task ARename_MovesTheFolder_AndTheProjectFollows()
    {
        var result = await _ws.RenameProjectAsync("beta", null, "gamma", renameFolder: true, migrateHistory: false);

        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(Path.Combine(_projects, "gamma")));
        Assert.False(Directory.Exists(Path.Combine(_projects, "beta")));
        Assert.Contains("gamma", (await _ws.ListProjectsAsync()).Select(p => p.Name));
    }

    [Fact]
    public async Task AFreshClone_AppearsOnTheNextRescan_AndAQuietRootChangesNothing()
    {
        Assert.False(await _ws.RescanIfChangedAsync());

        MakeProject("delta");

        Assert.True(await _ws.RescanIfChangedAsync());
        Assert.Contains("delta", (await _ws.ListProjectsAsync()).Select(p => p.Name));
        Assert.False(await _ws.RescanIfChangedAsync());
    }
}
