using System.IO;
using System.Text.Json;

namespace Firepit.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    // Installer writes this file next to settings.json before the first launch.
    // Contents: a single line containing the chosen projectsRoot. We consume it
    // exactly once (writes a full settings.json from defaults + the override,
    // then deletes the marker).
    private const string FirstRunMarkerName = "first-run-projects-root.txt";

    public JsonSettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultPath();
    }

    public string SettingsPath { get; }

    public FirepitSettings Load()
    {
        ApplyFirstRunMarkerIfPresent();

        if (!File.Exists(SettingsPath))
        {
            return FirepitSettings.Defaults;
        }

        try
        {
            using var stream = File.OpenRead(SettingsPath);
            var loaded = JsonSerializer.Deserialize(stream, FirepitJsonContext.Default.FirepitSettings);
            return loaded ?? FirepitSettings.Defaults;
        }
        catch (JsonException)
        {
            return FirepitSettings.Defaults;
        }
        catch (IOException)
        {
            return FirepitSettings.Defaults;
        }
    }

    public void Save(FirepitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        using var stream = File.Create(SettingsPath);
        JsonSerializer.Serialize(stream, settings, FirepitJsonContext.Default.FirepitSettings);
    }

    private void ApplyFirstRunMarkerIfPresent()
    {
        if (File.Exists(SettingsPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(SettingsPath)!;
        var markerPath = Path.Combine(dir, FirstRunMarkerName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        try
        {
            var root = File.ReadAllText(markerPath).Trim();
            if (!string.IsNullOrEmpty(root))
            {
                Save(FirepitSettings.Defaults with { ProjectsRoot = root });
            }
        }
        catch (IOException) { /* best effort — leave marker for next launch */ }
        finally
        {
            try { File.Delete(markerPath); } catch (IOException) { /* ignored */ }
        }
    }

    private static string DefaultPath() => Path.Combine(FirepitPaths.Roaming, "settings.json");
}
