using System.Text.Json;

namespace Deckwraith.Mcp;

public sealed record McpServerDefinition(
    string Id,
    string Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentReferences,
    int RequestTimeoutSeconds = 60);

public sealed record McpServerRegistry(
    int SchemaVersion,
    IReadOnlyList<McpServerDefinition> Servers,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static McpServerRegistry Empty(DateTimeOffset now) =>
        new(CurrentSchemaVersion, [], now);
}

public sealed record McpAssignmentDocument(
    int SchemaVersion,
    IReadOnlyList<string> IncludeServers,
    IReadOnlyList<string> IncludeTools,
    IReadOnlyList<string> ExcludeServers,
    IReadOnlyList<string> ExcludeTools,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static McpAssignmentDocument Empty(DateTimeOffset now) =>
        new(CurrentSchemaVersion, [], [], [], [], now);
}

public sealed record McpDiscoveredTool(
    string Name,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record McpCatalogEntry(
    string QualifiedName,
    string ServerId,
    string ToolName,
    string PowerShellModule,
    string PowerShellCommand,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record McpEffectiveCatalog(
    int SchemaVersion,
    string Wraith,
    string ContentHash,
    IReadOnlyList<McpCatalogEntry> Tools,
    DateTimeOffset RefreshedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record McpInvocationContext(
    string Wraith,
    string? Haunt,
    string? RunId,
    string? ShellId,
    string OperationId);

public sealed record McpToolCallResult(
    bool IsError,
    JsonElement StructuredContent,
    JsonElement Content,
    JsonElement RawResult);

public sealed class McpProtocolException : Exception
{
    public McpProtocolException(string message)
        : base(message)
    {
    }
}
