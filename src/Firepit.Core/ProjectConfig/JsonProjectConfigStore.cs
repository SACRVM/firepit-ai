using System.IO;
using System.Text.Json;

namespace Firepit.Core.ProjectConfig;

public sealed class JsonProjectConfigStore : IProjectConfigStore
{
    public const string DirectoryName = ".firepit";
    public const string FileName      = "config.json";

    public ProjectConfig? Load(string projectPath) => Read(projectPath).Config;

    public ProjectConfigRead Read(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        var path = ResolvePath(projectPath);
        if (!File.Exists(path))
        {
            return new ProjectConfigRead(null, Exists: false);
        }

        try
        {
            using var stream = File.OpenRead(path);
            var config = JsonSerializer.Deserialize(stream, ProjectConfigJsonContext.Default.ProjectConfig);
            return new ProjectConfigRead(config, Exists: true, config is null ? "the file holds no config object" : null);
        }
        catch (JsonException ex)
        {
            // Malformed: readers treat it as "no config"; writers must not
            // write over it (ProjectConfigRead.IsUnreadable).
            return new ProjectConfigRead(null, Exists: true, ex.Message);
        }
        catch (IOException ex)
        {
            return new ProjectConfigRead(null, Exists: true, ex.Message);
        }
    }

    public void Save(string projectPath, ProjectConfig config)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(config);

        var path = ResolvePath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, ProjectConfigJsonContext.Default.ProjectConfig) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    public static string ResolvePath(string projectPath) =>
        Path.Combine(projectPath, DirectoryName, FileName);
}
