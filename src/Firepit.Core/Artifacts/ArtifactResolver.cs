using System.IO;

namespace Firepit.Core.Artifacts;

/// <summary>
/// Turns stored <see cref="ArtifactEntry"/> records into
/// <see cref="ResolvedArtifact"/>s: relative paths become absolute against the
/// project root, the label falls back to the file name, and the extension is
/// classified for icon + click behaviour.
///
/// A missing target is resolved, not dropped — a link whose file was moved or
/// deleted stays visible (greyed out) so the user can see what happened and
/// remove it deliberately. Silently vanishing entries would look like data loss.
/// </summary>
public static class ArtifactResolver
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico" };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".json", ".jsonc", ".yaml", ".yml", ".csv", ".xml", ".ini", ".diff", ".patch" };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".rtf" };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".ps1", ".msi", ".com", ".scr", ".lnk" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2" };

    public static ResolvedArtifact Resolve(ArtifactEntry entry, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(projectPath);

        var absolute = ToAbsolute(entry.Path, projectPath);
        var label = string.IsNullOrWhiteSpace(entry.Label)
            ? FileNameOf(absolute)
            : entry.Label;

        return new ResolvedArtifact(
            Path:         entry.Path,
            AbsolutePath: absolute,
            Label:        label,
            Note:         entry.Note,
            AddedAtUtc:   entry.AddedAtUtc,
            Kind:         Classify(absolute),
            Exists:       Exists(absolute));
    }

    public static IReadOnlyList<ResolvedArtifact> ResolveAll(
        IReadOnlyList<ArtifactEntry> entries, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var result = new List<ResolvedArtifact>(entries.Count);
        foreach (var entry in entries)
        {
            result.Add(Resolve(entry, projectPath));
        }
        return result;
    }

    /// <summary>
    /// Classification by extension. A directory is <see cref="ArtifactKind.Other"/> —
    /// the pane opens it in Explorer rather than pretending to preview it.
    /// </summary>
    public static ArtifactKind Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return ArtifactKind.Other;
        }
        if (ImageExtensions.Contains(ext))      return ArtifactKind.Image;
        if (MarkdownExtensions.Contains(ext))   return ArtifactKind.Markdown;
        if (TextExtensions.Contains(ext))       return ArtifactKind.Text;
        if (DocumentExtensions.Contains(ext))   return ArtifactKind.Document;
        if (ExecutableExtensions.Contains(ext)) return ArtifactKind.Executable;
        if (ArchiveExtensions.Contains(ext))    return ArtifactKind.Archive;
        return ArtifactKind.Other;
    }

    /// <summary>
    /// Absolute path for <paramref name="path"/>, treating a relative value as
    /// project-root-relative. Malformed paths are returned unchanged rather
    /// than throwing — the caller renders them as missing.
    /// </summary>
    public static string ToAbsolute(string path, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        try
        {
            return System.IO.Path.IsPathRooted(path)
                ? System.IO.Path.GetFullPath(path)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectPath, path));
        }
        catch (ArgumentException)   { return path; }
        catch (NotSupportedException) { return path; }
        catch (PathTooLongException)  { return path; }
    }

    private static string FileNameOf(string absolute)
    {
        try
        {
            var name = System.IO.Path.GetFileName(absolute.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? absolute : name;
        }
        catch (ArgumentException) { return absolute; }
    }

    private static bool Exists(string absolute)
    {
        if (string.IsNullOrEmpty(absolute))
        {
            return false;
        }
        try
        {
            return File.Exists(absolute) || Directory.Exists(absolute);
        }
        catch (IOException)                  { return false; }
        catch (UnauthorizedAccessException)  { return false; }
    }
}
