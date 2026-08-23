namespace Deckwraith.Core.Runs;

public enum RunStatus
{
    Created,
    Running,
    AwaitingInput,
    Completed,
    Blocked,
    Failed,
    Cancelled,
}

public sealed record ShellDocument(
    string ShellId,
    string Provider,
    string Model,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason);

public sealed record RunDocument(
    int SchemaVersion,
    string RunId,
    string Agent,
    string? Haunt,
    string Objective,
    RunStatus Status,
    string? StatusReason,
    IReadOnlyList<ShellDocument> Shells,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}
