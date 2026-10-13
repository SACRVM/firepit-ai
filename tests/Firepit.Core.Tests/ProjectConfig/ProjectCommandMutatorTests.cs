using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Tests.ProjectConfig;

public class ProjectCommandMutatorTests
{
    private static ProjectCommand Shell(string name, string command) =>
        new(Name: name, Type: ProjectCommandType.Shell, Command: command);

    [Fact]
    public void Upsert_AppendsWhenNameIsNew()
    {
        var existing = new[] { Shell("Tests", "pwsh") };
        var added    = Shell("Dev", "npm");

        var result = ProjectCommandMutator.Upsert(existing, added);

        Assert.Equal(2, result.Count);
        Assert.Equal("Tests", result[0].Name);
        Assert.Equal("Dev",   result[1].Name);
    }

    [Fact]
    public void Upsert_ReplacesByName_PreservingPosition()
    {
        var existing = new[]
        {
            Shell("Tests", "pwsh"),
            Shell("Dev",   "npm"),
            Shell("Build", "dotnet"),
        };
        // Replacing the middle entry — should keep index 1, not jump to the end.
        var updated = Shell("Dev", "pnpm");

        var result = ProjectCommandMutator.Upsert(existing, updated);

        Assert.Equal(3, result.Count);
        Assert.Equal("Tests", result[0].Name);
        Assert.Equal("Dev",   result[1].Name);
        Assert.Equal("pnpm",  result[1].Command);
        Assert.Equal("Build", result[2].Name);
    }

    [Fact]
    public void Upsert_NameMatchIsCaseInsensitive()
    {
        var existing = new[] { Shell("Dev", "npm") };
        var updated  = Shell("DEV", "pnpm");

        var result = ProjectCommandMutator.Upsert(existing, updated);

        Assert.Single(result);
        Assert.Equal("DEV",  result[0].Name);
        Assert.Equal("pnpm", result[0].Command);
    }

    [Fact]
    public void Upsert_NullExistingList_ReturnsSingletonWithNewCommand()
    {
        var added  = Shell("Tests", "pwsh");
        var result = ProjectCommandMutator.Upsert(null, added);
        Assert.Single(result);
        Assert.Equal("Tests", result[0].Name);
    }

    [Fact]
    public void Upsert_EmptyExistingList_ReturnsSingletonWithNewCommand()
    {
        var result = ProjectCommandMutator.Upsert(Array.Empty<ProjectCommand>(), Shell("Tests", "pwsh"));
        Assert.Single(result);
    }

    [Fact]
    public void RemoveByName_DropsMatch_PreservesOthersInOrder()
    {
        var existing = new[]
        {
            Shell("Tests", "pwsh"),
            Shell("Dev",   "npm"),
            Shell("Build", "dotnet"),
        };

        var (result, removed) = ProjectCommandMutator.RemoveByName(existing, "Dev");

        Assert.True(removed);
        Assert.Equal(2, result.Count);
        Assert.Equal("Tests", result[0].Name);
        Assert.Equal("Build", result[1].Name);
    }

    [Fact]
    public void RemoveByName_CaseInsensitiveMatch()
    {
        var existing = new[] { Shell("Dev", "npm") };
        var (result, removed) = ProjectCommandMutator.RemoveByName(existing, "DEV");
        Assert.True(removed);
        Assert.Empty(result);
    }

    [Fact]
    public void RemoveByName_UnknownName_ReturnsUnchangedAndFalse()
    {
        var existing = new[] { Shell("Dev", "npm") };
        var (result, removed) = ProjectCommandMutator.RemoveByName(existing, "Ghost");
        Assert.False(removed);
        Assert.Single(result);
        Assert.Equal("Dev", result[0].Name);
    }

    [Fact]
    public void RemoveByName_NullExistingList_ReturnsEmptyAndFalse()
    {
        var (result, removed) = ProjectCommandMutator.RemoveByName(null, "Dev");
        Assert.False(removed);
        Assert.Empty(result);
    }

    [Fact]
    public void RemoveByName_OnlyFirstDuplicateRemoved()
    {
        // Defensive: shouldn't happen in practice (Upsert prevents dupes), but if a
        // hand-edited config has two same-named entries, we remove just the first
        // so the user notices and cleans up the other one explicitly.
        var existing = new[]
        {
            Shell("Dev", "npm"),
            Shell("Dev", "pnpm"),
        };

        var (result, removed) = ProjectCommandMutator.RemoveByName(existing, "Dev");

        Assert.True(removed);
        Assert.Single(result);
        Assert.Equal("pnpm", result[0].Command);
    }

    private static ProjectCommand Grouped(string group, string name, string command) =>
        new(Name: name, Type: ProjectCommandType.Shell, Command: command, Group: group);

    [Fact]
    public void Upsert_SameNameInAnotherGroup_IsAnotherCommand()
    {
        var existing = new[] { Grouped("Build", "debug", "dotnet") };

        var result = ProjectCommandMutator.Upsert(existing, Grouped("Run", "debug", "dotnet"));

        Assert.Equal(["Build debug", "Run debug"], result.Select(ProjectCommandMutator.PhraseOf));
    }

    [Fact]
    public void Upsert_SameGroupAndName_ReplacesInPlace()
    {
        var existing = new[] { Grouped("Git", "status", "git"), Grouped("Git", "repo", "xdg-open") };

        var result = ProjectCommandMutator.Upsert(existing, Grouped("git", "STATUS", "jj"));

        Assert.Equal(2, result.Count);
        Assert.Equal("jj", result[0].Command);
    }

    [Fact]
    public void RemoveByName_ThePhraseWins()
    {
        var existing = new[] { Grouped("Build", "debug", "a"), Grouped("Run", "debug", "b") };

        var (result, removed) = ProjectCommandMutator.RemoveByName(existing, "run debug");

        Assert.True(removed);
        Assert.Equal("a", Assert.Single(result).Command);
    }

    [Fact]
    public void Ambiguous_NamesEveryGroupThatHasTheName()
    {
        var existing = new[] { Grouped("Build", "debug", "a"), Grouped("Run", "debug", "b"), Grouped("Git", "status", "c") };

        Assert.Equal(["Build debug", "Run debug"], ProjectCommandMutator.Ambiguous(existing, "debug"));
        Assert.Empty(ProjectCommandMutator.Ambiguous(existing, "status"));
        Assert.Empty(ProjectCommandMutator.Ambiguous(existing, "Run debug"));
        Assert.Empty(ProjectCommandMutator.Ambiguous(existing, "ghost"));
    }
}
