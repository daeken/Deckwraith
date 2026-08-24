using System.Text.Json;

namespace Deckwraith.Core.Context;

public sealed record CompactionDocument(
    int SchemaVersion,
    string CompactionId,
    string Agent,
    long FirstSequence,
    long LastSequence,
    IReadOnlyList<string> SourceContentHashes,
    string? PreviousCompactionId,
    string Provider,
    string Model,
    string PromptVersion,
    JsonElement Parameters,
    string Summary,
    IReadOnlyList<string> UnresolvedItems,
    IReadOnlyList<string> ArtifactReferences,
    DateTimeOffset CreatedAt,
    bool IsValid,
    string? ValidationError,
    string? CheckpointCommit)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CompactionSelection(
    long FirstSequence,
    long LastSequence,
    IReadOnlyList<string> SourceContentHashes);

public sealed record CompactionResult(
    CompactionDocument Compaction,
    CurrentContextDocument Context,
    string CommitId);
