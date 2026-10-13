using Firepit.Core.Mcp;

namespace Firepit.Core.Tests.Mcp;

public class McpHealthCheckerTests
{
    private static ResolvedMcpServer Stdio(string id, string? command) => new(
        Id: id,
        DisplayName: id,
        Transport: McpTransport.Stdio,
        Command: command,
        Args: [],
        Environment: new Dictionary<string, string>(),
        Url: null,
        Headers: new Dictionary<string, string>(),
        ResolutionWarnings: []);

    private static ResolvedMcpServer Http(string id, string? url) => new(
        Id: id,
        DisplayName: id,
        Transport: McpTransport.Http,
        Command: null,
        Args: [],
        Environment: new Dictionary<string, string>(),
        Url: url,
        Headers: new Dictionary<string, string>(),
        ResolutionWarnings: []);

    [Fact]
    public void Check_ReportsMissingBareCommand()
    {
        var checker = new McpHealthChecker(
            readPath:    () => @"C:\Tools",
            readPathExt: () => ".EXE;.CMD",
            fileExists:  _ => false);

        var issues = checker.Check([Stdio("firepit", "firepit-mcp")]);

        Assert.Single(issues);
        Assert.Equal("firepit", issues[0].ServerId);
        Assert.Equal(McpHealthIssueKind.CommandNotFound, issues[0].Kind);
    }

    [UnixFact]
    public void Check_ResolvesBareCommandOnThePath()
    {
        var checker = new McpHealthChecker(
            readPath:    () => "/usr/local/bin:/home/u/.local/bin",
            readPathExt: () => null,
            fileExists:  p => p == "/home/u/.local/bin/firepit-mcp");

        Assert.Empty(checker.Check([Stdio("firepit", "firepit-mcp")]));
    }

    [WindowsFact]
    public void Check_ResolvesBareCommandViaPathExt()
    {
        var resolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Tools\firepit-mcp.exe",
        };

        var checker = new McpHealthChecker(
            readPath:    () => @"C:\Tools",
            readPathExt: () => ".EXE;.CMD",
            fileExists:  p => resolvable.Contains(p));

        var issues = checker.Check([Stdio("firepit", "firepit-mcp")]);
        Assert.Empty(issues);
    }

    [Fact]
    public void Check_AbsolutePath_IgnoresPathLookup()
    {
        var resolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\custom\bridge.exe",
        };

        var checker = new McpHealthChecker(
            readPath:    () => @"C:\Tools",
            readPathExt: () => ".EXE",
            fileExists:  p => resolvable.Contains(p));

        var issues = checker.Check([Stdio("custom", @"D:\custom\bridge.exe")]);
        Assert.Empty(issues);
    }

    [Fact]
    public void Check_AbsolutePathMissing_IsReported()
    {
        var checker = new McpHealthChecker(
            readPath:    () => string.Empty,
            readPathExt: () => ".EXE",
            fileExists:  _ => false);

        var issues = checker.Check([Stdio("custom", @"D:\nope\bridge.exe")]);
        Assert.Single(issues);
    }

    [Fact]
    public void Check_HttpTransport_IsSkipped()
    {
        var checker = new McpHealthChecker(
            readPath:    () => string.Empty,
            readPathExt: () => string.Empty,
            fileExists:  _ => false);

        var issues = checker.Check([Http("remote", "https://example.com/mcp")]);
        Assert.Empty(issues);
    }

    [Fact]
    public void Check_NullCommand_IsSkipped()
    {
        var checker = new McpHealthChecker(
            readPath:    () => string.Empty,
            readPathExt: () => string.Empty,
            fileExists:  _ => false);

        var issues = checker.Check([Stdio("oops", null)]);
        Assert.Empty(issues);
    }
}
