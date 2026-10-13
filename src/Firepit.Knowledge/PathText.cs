namespace Firepit.Knowledge;

/// <summary>
/// How paths compare on this OS: case-insensitively on Windows and macOS's
/// default volumes, exactly everywhere else. Knowledge is self-contained, so it
/// carries its own copy of what Firepit.Core's <c>PathComparison</c> does.
/// </summary>
internal static class PathText
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
