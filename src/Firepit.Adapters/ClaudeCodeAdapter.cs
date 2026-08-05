using System.Text;
using Firepit.Core.Agents;
using Firepit.Core.Projects;

namespace Firepit.Adapters;

public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    public const string AdapterId = "claude-code";

    private readonly string _defaultExecutable;

    public ClaudeCodeAdapter(string defaultExecutable = "claude")
    {
        _defaultExecutable = defaultExecutable;
    }

    public string Id => AdapterId;

    public string DisplayName => "Claude Code";

    public IReadOnlyList<string> ProjectMarkers { get; } = ["CLAUDE.md", ".claude"];

    public AgentLaunchSpec BuildLaunchSpec(ProjectContext context, AgentLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var executable = context.Project.AgentCommandOverride ?? _defaultExecutable;
        var arguments = new List<string>();

        if (context.Project.AgentArgsOverride is { } overrides)
        {
            arguments.AddRange(overrides);
        }

        if (options.Resume)
        {
            arguments.Add("--continue");
        }

        if (!string.IsNullOrEmpty(options.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(options.SessionId);
        }

        return new AgentLaunchSpec(
            Executable: executable,
            Arguments: arguments,
            WorkingDirectory: context.Path);
    }

    /// <summary>
    /// Claude Code keeps one transcript folder per working directory under
    /// <c>~/.claude/projects</c>, named after the absolute path with every
    /// non-alphanumeric character flattened to '-'
    /// (<c>D:\repos\foo</c> → <c>D--repos-foo</c>). A <c>*.jsonl</c> inside
    /// means <c>--continue</c> has a conversation to pick up.
    /// </summary>
    public bool HasResumableSession(ProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", EncodeProjectDir(context.Path));
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.jsonl").Any();
        }
        catch (Exception)
        {
            // Unreadable home dir etc. — treat as "nothing to resume".
            return false;
        }
    }

    private static string EncodeProjectDir(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }
        return sb.ToString();
    }
}
