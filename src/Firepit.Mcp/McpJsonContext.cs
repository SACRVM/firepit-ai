using System.Text.Json;
using System.Text.Json.Serialization;

namespace Firepit.Mcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(IReadOnlyList<ProjectInfo>))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(IReadOnlyList<SessionInfo>))]
[JsonSerializable(typeof(ToolCallResult))]
[JsonSerializable(typeof(InboxWriteResult))]
[JsonSerializable(typeof(InboxMessage))]
[JsonSerializable(typeof(IReadOnlyList<InboxMessage>))]
[JsonSerializable(typeof(InboxListResult))]
[JsonSerializable(typeof(CommandSummary))]
[JsonSerializable(typeof(IReadOnlyList<CommandSummary>))]
[JsonSerializable(typeof(CommandListResult))]
[JsonSerializable(typeof(KnowledgeHitInfo))]
[JsonSerializable(typeof(IReadOnlyList<KnowledgeHitInfo>))]
[JsonSerializable(typeof(KnowledgeSearchResult))]
[JsonSerializable(typeof(KnowledgeDocumentResult))]
[JsonSerializable(typeof(CreateProjectResult))]
[JsonSerializable(typeof(RenameProjectResult))]
[JsonSerializable(typeof(BlueprintInfo))]
[JsonSerializable(typeof(IReadOnlyList<BlueprintInfo>))]
[JsonSerializable(typeof(BlueprintListResult))]
[JsonSerializable(typeof(BlueprintProjectCheck))]
[JsonSerializable(typeof(IReadOnlyList<BlueprintProjectCheck>))]
[JsonSerializable(typeof(BlueprintCheckResult))]
[JsonSerializable(typeof(BlueprintApplyResult))]
[JsonSerializable(typeof(ArtifactSummary))]
[JsonSerializable(typeof(IReadOnlyList<ArtifactSummary>))]
[JsonSerializable(typeof(ArtifactListResult))]
[JsonSerializable(typeof(McpResourceDefinition))]
[JsonSerializable(typeof(IReadOnlyList<McpResourceDefinition>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
