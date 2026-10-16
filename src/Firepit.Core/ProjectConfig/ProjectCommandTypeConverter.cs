using System.Text.Json;
using System.Text.Json.Serialization;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// A command's <c>type</c>: read in any case, with or without the dash, since
/// the docs and the commands mod write <c>shell</c>, <c>claude-prompt</c> and
/// <c>url</c> while the Windows shell wrote <c>Shell</c> and <c>ClaudePrompt</c>;
/// written the way the docs spell it. The default enum converter knew only the
/// member names, so one <c>claude-prompt</c> made a whole config unreadable.
/// </summary>
public sealed class ProjectCommandTypeConverter : JsonConverter<ProjectCommandType>
{
    public override ProjectCommandType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A command type is a string: shell, claude-prompt or url.");
        }

        var text = reader.GetString() ?? "";
        return text.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "shell"        => ProjectCommandType.Shell,
            "claudeprompt" => ProjectCommandType.ClaudePrompt,
            "url"          => ProjectCommandType.Url,
            _ => throw new JsonException($"Unknown command type '{text}': use shell, claude-prompt or url."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ProjectCommandType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Name(value));

    public static string Name(ProjectCommandType value) => value switch
    {
        ProjectCommandType.Shell        => "shell",
        ProjectCommandType.ClaudePrompt => "claude-prompt",
        ProjectCommandType.Url          => "url",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
