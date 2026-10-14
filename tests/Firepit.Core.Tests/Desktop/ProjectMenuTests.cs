using System.IO;
using Firepit.Core.Desktop;

namespace Firepit.Core.Tests.Desktop;

public class ProjectMenuTests : IDisposable
{
    private const string Launcher = "/home/me/.local/opt/firepit/firepitd";
    private readonly string _root;
    private readonly ProjectMenuLocations _at;

    public ProjectMenuTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "firepit-menu-tests", Guid.NewGuid().ToString("N"));
        _at = new ProjectMenuLocations(
            Path.Combine(_root, "applications", "firepit"),
            Path.Combine(_root, "desktop-directories"),
            Path.Combine(_root, "menus", "applications-merged"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string[] Entries() =>
        Directory.GetFiles(_at.Applications).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void EveryProject_GetsAnEntry_InTheProjectsSubmenu()
    {
        var changed = ProjectMenu.Sync(
            [("vectorizer", "/r/vectorizer"), (".firepit", "/r/.firepit"), ("CM", "/r/CM")], Launcher, _at);

        Assert.True(changed);
        Assert.Equal(["cm.desktop", "firepit-central.desktop", "vectorizer.desktop"], Entries());

        var entry = File.ReadAllText(Path.Combine(_at.Applications, "vectorizer.desktop"));
        Assert.Contains("Name=vectorizer\n", entry);
        Assert.Contains($"Exec=\"{Launcher}\" open \"/r/vectorizer\"\n", entry);
        Assert.Contains($"Categories={ProjectMenu.Category};", entry);
        Assert.Contains("Name=Firepit central\n", File.ReadAllText(Path.Combine(_at.Applications, "firepit-central.desktop")));

        var menu = File.ReadAllText(Path.Combine(_at.MergedMenus, ProjectMenu.MenuFileName));
        Assert.Contains("<Name>Projects</Name>", menu);
        Assert.Contains($"<Category>{ProjectMenu.Category}</Category>", menu);
        Assert.Contains("Name=Projects", File.ReadAllText(Path.Combine(_at.Directories, ProjectMenu.DirectoryFileName)));
    }

    [Fact]
    public void ASecondSync_WithTheSameProjects_ChangesNothing()
    {
        ProjectMenu.Sync([("a", "/r/a")], Launcher, _at);

        Assert.False(ProjectMenu.Sync([("a", "/r/a")], Launcher, _at));
    }

    [Fact]
    public void AProjectThatIsGone_LosesItsEntry_AndNothingElseIsTouched()
    {
        ProjectMenu.Sync([("a", "/r/a"), ("b", "/r/b")], Launcher, _at);
        File.WriteAllText(Path.Combine(_at.Applications, "notes.txt"), "someone else's");

        Assert.True(ProjectMenu.Sync([("a", "/r/a")], Launcher, _at));
        Assert.Equal(["a.desktop", "notes.txt"], Entries());
    }

    [Fact]
    public void TwoNamesWithTheSameFileName_BothGetAnEntry()
    {
        ProjectMenu.Sync([("My App", "/r/my-app-1"), ("my-app", "/r/my-app-2")], Launcher, _at);

        Assert.Equal(["my-app-2.desktop", "my-app.desktop"], Entries());
    }

    [Fact]
    public void ThePathIsQuotedForTheExecLine()
    {
        var entry = ProjectMenu.Entry("odd", "/r/a b/$x \"q\" 100%", Launcher);

        Assert.Contains("Exec=\"/home/me/.local/opt/firepit/firepitd\" open \"/r/a b/\\\\$x \\\\\"q\\\\\" 100%%\"\n", entry);
    }
}
