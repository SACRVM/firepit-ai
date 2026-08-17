namespace Firepit.Knowledge;

/// <summary>
/// Resolves where one project's knowledge docs live.
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;project&gt;/.firepit/knowledge</c> is either a <b>directory</b>, and the
/// docs are in it, or a <b>file</b>, and its content is the path to the
/// directory that holds them. Same name, two filesystem types — the pattern
/// git itself uses, where <c>.git</c> is a directory in a normal clone and a
/// file containing <c>gitdir:</c> in a worktree or submodule.
/// </para>
/// <para>
/// This replaces a config key on purpose. A key needs a value space, a value
/// space needs reserved words, and reserved words collide with names. A path
/// in a file has none of that, and there is nowhere to write a value Firepit
/// does not support.
/// </para>
/// <para>
/// A pointer must land on a directory. Pointing at another pointer is an
/// error, not a chain to follow — which is what keeps resolution to exactly
/// one step, with no hop limit and no cycles to detect.
/// </para>
/// </remarks>
public static class KnowledgeLocator
{
    public const string FileName = "knowledge";

    // A pointer file holds a path. Anything substantially larger is a file
    // that landed here by accident, and reading it whole would be the wrong
    // way to find that out.
    private const int MaxPointerBytes = 8 * 1024;

    /// <param name="DocsDir">Absolute path to the directory holding the scope's markdown.</param>
    /// <param name="IsRedirected">True when a pointer file sent the docs outside the project.</param>
    /// <param name="Error">Set when the pointer could not be honoured. <see cref="DocsDir"/> is then meaningless.</param>
    public sealed record Resolution(string DocsDir, bool IsRedirected, string? Error = null);

    public static Resolution Resolve(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var firepitDir = Path.Combine(Path.GetFullPath(projectPath), ".firepit");
        var knowledge = Path.Combine(firepitDir, FileName);

        // A directory, or nothing yet: the docs belong to the project. The
        // "nothing yet" case resolves to the same path so a first write lands
        // where a project that never opted in would expect.
        if (Directory.Exists(knowledge) || !File.Exists(knowledge))
        {
            return new Resolution(knowledge, IsRedirected: false);
        }

        string content;
        try
        {
            var info = new FileInfo(knowledge);
            if (info.Length > MaxPointerBytes)
            {
                return Fail(
                    $"'{knowledge}' is a file of {info.Length} bytes. A pointer holds one path — " +
                    "make it a directory to keep the docs here, or put a path in it.");
            }

            content = File.ReadAllText(knowledge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read '{knowledge}': {ex.Message}");
        }

        var target = FirstMeaningfulLine(content);
        if (target is null)
        {
            return Fail(
                $"'{knowledge}' is empty. Put the path to the knowledge directory in it, " +
                "or delete it and create a directory of that name instead.");
        }

        // Relative to the .firepit directory holding the pointer, so moving
        // the whole repos tree keeps every pointer valid.
        var resolved = Path.GetFullPath(Path.Combine(firepitDir, target));

        if (File.Exists(resolved))
        {
            return Fail(
                $"'{knowledge}' points at '{resolved}', which is a file. A pointer must name a " +
                "directory — pointing at another pointer is not supported.");
        }

        if (!Directory.Exists(resolved))
        {
            // Discriminate a typo from a directory that simply has not been
            // written to yet: if the parent is missing the path is wrong, and
            // saying so beats silently indexing an empty knowledge base.
            var parent = Path.GetDirectoryName(resolved);
            if (parent is null || !Directory.Exists(parent))
            {
                return Fail(
                    $"'{knowledge}' points at '{resolved}', which does not exist and neither does " +
                    "its parent. Check the path — it is relative to the .firepit directory.");
            }
        }

        return new Resolution(resolved, IsRedirected: true);

        Resolution Fail(string message) => new(knowledge, false, message);
    }

    private static string? FirstMeaningfulLine(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            return line;
        }

        return null;
    }
}
