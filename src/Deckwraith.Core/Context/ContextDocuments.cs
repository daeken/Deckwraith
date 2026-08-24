using System.Text.Json;
using Deckwraith.Core.Naming;

namespace Deckwraith.Core.Context;

public enum ContextItemKind
{
    Message,
    ToolInteraction,
    ToolElision,
    Compaction,
}

public enum ContextRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum OperationStatus
{
    Started,
    Completed,
    Failed,
    Cancelled,
    OutcomeUnknown,
}

public sealed record ContextItem(
    string ItemId,
    ContextItemKind Kind,
    ContextRole? Role,
    string? Text,
    string? OperationId,
    string? Tool,
    OperationStatus? Status,
    int? CompletedAtTurn,
    JsonElement? Input,
    JsonElement? Output,
    long ArchiveFirstSequence,
    long ArchiveLastSequence)
{
    public static ContextItem Message(
        string itemId,
        ContextRole role,
        string text,
        long archiveSequence) =>
        new(
            itemId,
            ContextItemKind.Message,
            role,
            text,
            null,
            null,
            null,
            null,
            null,
            null,
            archiveSequence,
            archiveSequence);

    public static ContextItem ToolInteraction(
        string itemId,
        string operationId,
        string tool,
        OperationStatus status,
        int completedAtTurn,
        JsonElement input,
        JsonElement output,
        long archiveFirstSequence,
        long archiveLastSequence) =>
        new(
            itemId,
            ContextItemKind.ToolInteraction,
            null,
            null,
            operationId,
            tool,
            status,
            completedAtTurn,
            input.Clone(),
            output.Clone(),
            archiveFirstSequence,
            archiveLastSequence);

    public static ContextItem Compaction(
        string compactionId,
        string summary,
        long archiveFirstSequence,
        long archiveLastSequence) =>
        new(
            compactionId,
            ContextItemKind.Compaction,
            ContextRole.System,
            summary,
            null,
            null,
            null,
            null,
            null,
            null,
            archiveFirstSequence,
            archiveLastSequence);
}

public sealed record CurrentContextDocument(
    int SchemaVersion,
    string Agent,
    int Revision,
    int Turn,
    long ArchiveFrontier,
    string IdentityHash,
    int DeckbookRevision,
    int ToolElisionTurns,
    IReadOnlyList<ContextItem> Items,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static CurrentContextDocument Create(
        CanonicalName agent,
        string identityHash,
        int toolElisionTurns,
        DateTimeOffset now) =>
        new(
            CurrentSchemaVersion,
            agent.Value,
            0,
            0,
            0,
            identityHash,
            0,
            toolElisionTurns,
            [],
            now);
}
