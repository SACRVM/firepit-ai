using System.IO;
using System.Text.Json;

namespace Firepit.Core.State;

public sealed class JsonStateStore : IStateStore
{
    public JsonStateStore(string? path = null)
    {
        StatePath = path ?? DefaultPath();
    }

    public string StatePath { get; }

    public AppState Load()
    {
        if (!File.Exists(StatePath))
        {
            return AppState.Empty;
        }

        try
        {
            using var stream = File.OpenRead(StatePath);
            var loaded = JsonSerializer.Deserialize(stream, StateJsonContext.Default.AppState);
            if (loaded is null || loaded.Version != AppState.CurrentVersion)
            {
                // future migration hook — treat unknown versions as empty for V1
                return AppState.Empty;
            }
            return loaded;
        }
        catch (JsonException)
        {
            return AppState.Empty;
        }
        catch (IOException)
        {
            return AppState.Empty;
        }
    }

    public void Save(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stream = File.Create(StatePath);
        JsonSerializer.Serialize(stream, state, StateJsonContext.Default.AppState);
    }

    private static string DefaultPath() => Path.Combine(FirepitPaths.Local, "state.json");
}
