namespace Firepit.Core;

/// <summary>
/// String comparisons whose right answer depends on the OS. Paths and
/// environment variable names are case-insensitive on Windows and
/// case-sensitive on Linux; comparing them case-insensitively there makes
/// <c>/x/Foo</c> and <c>/x/foo</c> one project, and shares trust between them.
/// </summary>
public static class PlatformText
{
    private static readonly bool IsCaseInsensitiveFileSystem =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>How two paths compare.</summary>
    public static StringComparison Paths { get; } =
        IsCaseInsensitiveFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Keys collections of paths.</summary>
    public static StringComparer PathComparer { get; } =
        IsCaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Keys collections of environment variable names.</summary>
    public static StringComparer EnvironmentNames { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A path as a dictionary key part: folded where the file system folds case.</summary>
    public static string PathKey(string path) =>
        IsCaseInsensitiveFileSystem ? path.ToLowerInvariant() : path;
}
