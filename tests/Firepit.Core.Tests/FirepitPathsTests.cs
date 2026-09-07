using Firepit.Core;

namespace Firepit.Core.Tests;

/// <summary>
/// The naming rules only. Which instance a process *is* is ambient, set once
/// at startup, so it is not something a test can vary — but what a legal name
/// looks like, and what folders and pipes it produces, is pure and is where
/// the damage would be.
/// </summary>
public class FirepitPathsTests
{
    [Theory]
    [InlineData("dev")]
    [InlineData("beta")]
    [InlineData("test-2")]
    [InlineData("v0.28")]
    [InlineData("a_b")]
    public void ValidNames_AreAccepted(string name) =>
        Assert.True(FirepitPaths.IsValidInstanceName(name));

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NamesThatCouldLeaveTheProductFolder_AreRefused(string? name)
    {
        // The name becomes a directory name. A separator or a parent reference
        // in it would place an instance's data somewhere else entirely, so the
        // rule refuses rather than escapes.
        Assert.False(FirepitPaths.IsValidInstanceName(name));
    }

    [Theory]
    [InlineData(".hidden")]
    [InlineData("-instance")]
    [InlineData("_leading")]
    public void NamesNotStartingWithALetterOrDigit_AreRefused(string name)
    {
        // Rules out ".." without leaning on the "Firepit-" prefix to make it
        // harmless, and keeps a name from being read as another switch.
        Assert.False(FirepitPaths.IsValidInstanceName(name));
    }

    [Fact]
    public void TrailingDot_IsRefused()
    {
        // Windows strips it, so "beta." and "beta" would be the same folder
        // under two names — two instances quietly sharing one data root.
        Assert.False(FirepitPaths.IsValidInstanceName("beta."));
    }

    [Fact]
    public void NameLongerThanTheLimit_IsRefused() =>
        Assert.False(FirepitPaths.IsValidInstanceName(new string('a', 33)));

    [Fact]
    public void DefaultInstance_KeepsTheFoldersAnInstallationAlreadyUses()
    {
        // Not cosmetic: an installed Firepit that suddenly looked somewhere
        // else would present itself as having lost every project.
        Assert.Equal("Firepit", FirepitPaths.FolderNameFor(null));
        Assert.Equal("Firepit", FirepitPaths.FolderNameFor(""));
        Assert.Equal("firepit-singleton", FirepitPaths.SingletonPipeNameFor(null));
        Assert.Equal("firepit-mcp", FirepitPaths.McpPipeNameFor(null));
    }

    [Fact]
    public void NamedInstance_GetsItsOwnFolderAndBothPipes()
    {
        Assert.Equal("Firepit-dev", FirepitPaths.FolderNameFor("dev"));
        Assert.Equal("firepit-singleton-dev", FirepitPaths.SingletonPipeNameFor("dev"));
        Assert.Equal("firepit-mcp-dev", FirepitPaths.McpPipeNameFor("dev"));
    }

    [Fact]
    public void TheMcpPipeDiffersPerInstance()
    {
        // The one that matters most. Two Firepits on one MCP pipe both accept
        // connections, and a bridge reaches whichever Windows picks — an agent
        // could act on the wrong Firepit with nothing reporting it.
        Assert.NotEqual(FirepitPaths.McpPipeNameFor(null), FirepitPaths.McpPipeNameFor("dev"));
        Assert.NotEqual(FirepitPaths.McpPipeNameFor("dev"), FirepitPaths.McpPipeNameFor("beta"));
    }

    [Fact]
    public void SurroundingWhitespace_DoesNotProduceADifferentInstance()
    {
        Assert.Equal(FirepitPaths.FolderNameFor("dev"), FirepitPaths.FolderNameFor("  dev  "));
        Assert.Equal(FirepitPaths.McpPipeNameFor("dev"), FirepitPaths.McpPipeNameFor(" dev "));
    }
}
