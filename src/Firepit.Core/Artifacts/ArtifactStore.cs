using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Firepit.Core.Artifacts;

/// <summary>
/// On-disk shape of <c>&lt;projectPath&gt;/.firepit/artifacts.json</c>.
/// Versioned from day one so a later shape change can migrate rather than guess.
/// </summary>
public sealed record ArtifactFile(
    int Version = 1,
    IReadOnlyList<ArtifactEntry>? Artifacts = null);

/// <summary>
/// Reads and writes a project's artifact list. Lives next to config.json in
/// <c>.firepit/</c> — a separate file on purpose: artifacts churn constantly
/// (an agent adds one per report) while config.json is hand-curated, and a
/// project that gitignores the churn shouldn't have to ignore its config too.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Empty list when the file is absent or unreadable.</summary>
    IReadOnlyList<ArtifactEntry> Load(string projectPath);

    void Save(string projectPath, IReadOnlyList<ArtifactEntry> artifacts);
}

public sealed class JsonArtifactStore : IArtifactStore
{
    public const string DirectoryName = ".firepit";
    public const string FileName      = "artifacts.json";

    public IReadOnlyList<ArtifactEntry> Load(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        var path = ResolvePath(projectPath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var parsed = JsonSerializer.Deserialize(stream, ArtifactJsonContext.Default.ArtifactFile);
            return parsed?.Artifacts ?? [];
        }
        catch (JsonException)
        {
            // Malformed file: treat as empty rather than crashing the pane. The
            // file is left alone — a later Save would overwrite the user's
            // broken-but-recoverable JSON, so we never write over what we
            // couldn't read.
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public void Save(string projectPath, IReadOnlyList<ArtifactEntry> artifacts)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(artifacts);

        var path = ResolvePath(projectPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(
            stream,
            new ArtifactFile(Version: 1, Artifacts: artifacts),
            ArtifactJsonContext.Default.ArtifactFile);
    }

    public static string ResolvePath(string projectPath) =>
        System.IO.Path.Combine(projectPath, DirectoryName, FileName);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ArtifactFile))]
internal partial class ArtifactJsonContext : JsonSerializerContext
{
}
