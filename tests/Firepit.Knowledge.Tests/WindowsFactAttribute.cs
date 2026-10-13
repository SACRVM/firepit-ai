namespace Firepit.Knowledge.Tests;

/// <summary>
/// A fact about Windows behaviour (sharing violations, mapped images, drive
/// paths): runs on Windows, reported as skipped elsewhere.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only behaviour";
        }
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only behaviour";
        }
    }
}

/// <summary>A fact about Unix behaviour: runs on Linux and macOS, skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only behaviour";
        }
    }
}
