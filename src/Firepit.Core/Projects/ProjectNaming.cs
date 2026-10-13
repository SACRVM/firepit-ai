using System.IO;

namespace Firepit.Core.Projects;

/// <summary>
/// Derives a project's display name from its folder path.
/// </summary>
public static class ProjectNaming
{
    /// <summary>
    /// The last path segment, e.g. <c>C:\repos\foo</c> → <c>foo</c>,
    /// <c>/home/u/foo</c> → <c>foo</c>. Split on both separators by hand:
    /// <see cref="Path.GetFileName(string)"/> returns EMPTY for a UNC share
    /// root (<c>\\server\share</c>) and drive roots, and on Linux it does not
    /// see a backslash as a separator at all — a path recorded on Windows
    /// would come back whole.
    /// </summary>
    public static string DeriveName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : trimmed;
    }
}
