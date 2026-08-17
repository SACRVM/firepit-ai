namespace Firepit.Knowledge;

/// <summary>
/// Writes and removes the <c>.firepit/knowledge</c> pointer file, and moves the
/// docs along with it. The UI side of <see cref="KnowledgeLocator"/>.
/// </summary>
public static class KnowledgePointerFile
{
    public sealed record Plan(
        string TargetDir,
        int DocumentsToMove,
        string? Blocker = null)
    {
        public bool CanApply => Blocker is null;
    }

    /// <summary>
    /// What switching this project to <paramref name="targetDir"/> would do.
    /// Null target means "keep the docs in the repo".
    /// </summary>
    public static Plan Describe(string projectPath, string? targetDir)
    {
        var local = KnowledgeLayout.LocalDocsDir(projectPath);
        var destination = targetDir is null ? local : Path.GetFullPath(targetDir);

        if (PathsEqual(destination, local) && !IsPointerFile(local))
        {
            return new Plan(destination, 0);
        }

        var source = CurrentDocsDir(projectPath);
        var moving = CountDocs(source);

        // Merging two populated directories silently is how a document gets
        // overwritten by a same-named one from somewhere else. Refuse, and let
        // the user resolve it deliberately.
        if (moving > 0 && CountDocs(destination) > 0)
        {
            return new Plan(destination, moving,
                $"'{destination}' already holds documents. Move or remove them first — " +
                "Firepit will not merge two knowledge bases for you.");
        }

        return new Plan(destination, moving);
    }

    /// <summary>
    /// Points this project at <paramref name="targetDir"/>, or back at its own
    /// repo when the target is null. Moves any existing documents.
    /// </summary>
    public static void Apply(string projectPath, string? targetDir)
    {
        var plan = Describe(projectPath, targetDir);
        if (plan.Blocker is { } blocker)
        {
            throw new InvalidOperationException(blocker);
        }

        var firepitDir = Path.Combine(Path.GetFullPath(projectPath), ".firepit");
        var local = KnowledgeLayout.LocalDocsDir(projectPath);
        var source = CurrentDocsDir(projectPath);

        Directory.CreateDirectory(firepitDir);

        if (targetDir is null || PathsEqual(plan.TargetDir, local))
        {
            // The pointer has to go before the move, not after: the directory
            // the docs are coming back to wants the name the file is holding.
            // Only ever remove a *file* here — deleting a directory called
            // "knowledge" would take the documents with it.
            if (IsPointerFile(local))
            {
                File.Delete(local);
            }

            if (plan.DocumentsToMove > 0)
            {
                MoveDocs(source, plan.TargetDir);
            }

            return;
        }

        if (plan.DocumentsToMove > 0)
        {
            MoveDocs(source, plan.TargetDir);
        }

        if (Directory.Exists(local))
        {
            // Emptied by the move above; anything left is not ours to delete.
            if (Directory.EnumerateFileSystemEntries(local).Any())
            {
                throw new InvalidOperationException(
                    $"'{local}' is not empty. It has to go before a pointer file can take its name.");
            }

            Directory.Delete(local);
        }

        Directory.CreateDirectory(plan.TargetDir);
        var relative = Path.GetRelativePath(firepitDir, plan.TargetDir).Replace('\\', '/');
        File.WriteAllText(
            local,
            "# Firepit: this project's knowledge lives at the path below.\n" +
            "# Relative to this .firepit directory. Delete this file to keep the docs in the repo.\n" +
            relative + "\n");
    }

    private static string CurrentDocsDir(string projectPath)
    {
        var resolved = KnowledgeLocator.Resolve(projectPath);
        return resolved.Error is null ? resolved.DocsDir : KnowledgeLayout.LocalDocsDir(projectPath);
    }

    private static bool IsPointerFile(string knowledgePath) =>
        File.Exists(knowledgePath) && !Directory.Exists(knowledgePath);

    private static int CountDocs(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Count()
            : 0;

    private static void MoveDocs(string source, string destination)
    {
        if (PathsEqual(source, destination))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: false);
        }

        foreach (var dir in Directory
            .EnumerateDirectories(source, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
