namespace Firepit.Core;

/// <summary>
/// Gives a brand-new named instance something to work with.
/// </summary>
/// <remarks>
/// <para>
/// A named instance starts with an empty data root, which means no projects,
/// no MCP servers, no scan root — and a Firepit with nothing in it cannot be
/// used to check whether a change works. The whole reason for running one is
/// to try a build against real work, so the first run copies the installed
/// instance's <c>settings.json</c> across.
/// </para>
/// <para>
/// Settings only, and deliberately not <c>state.json</c>. State is the list of
/// tabs that were open, and restoring it would have a test instance launch an
/// agent process per project the moment it starts — real sessions, in real
/// repositories, that nobody asked for. Copying configuration is helpful;
/// copying what was running is not.
/// </para>
/// <para>
/// Once only. After the first run the instance owns its settings and may
/// diverge — that is the point of it being separate, and silently overwriting
/// them on every start would make it impossible to test a different
/// configuration.
/// </para>
/// </remarks>
public static class InstanceSeed
{
    /// <summary>
    /// Copy the installed instance's settings if this one has none yet.
    /// </summary>
    /// <returns>A line to log, or null when there was nothing to do.</returns>
    public static string? EnsureSettingsSeeded()
    {
        if (FirepitPaths.IsDefaultInstance)
        {
            return null;
        }

        var target = Path.Combine(FirepitPaths.Roaming, "settings.json");
        if (File.Exists(target))
        {
            return null;
        }

        var source = Path.Combine(FirepitPaths.DefaultRoaming, "settings.json");
        if (!File.Exists(source))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(FirepitPaths.Roaming);
            File.Copy(source, target);
            return $"Instance '{FirepitPaths.InstanceName}' seeded its settings from the installed " +
                   "Firepit. Open tabs were not copied; changes here do not travel back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting empty is worse than starting seeded, but it is not
            // broken — the instance still runs and the user can point it at a
            // project by hand.
            return $"Could not seed settings for instance '{FirepitPaths.InstanceName}': {ex.Message}";
        }
    }
}
