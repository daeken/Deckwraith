using System.Text.Json;

namespace Deckwraith.Core.Archives;

public sealed record ArchiveRecord(
    int SchemaVersion,
    string EventId,
    string Agent,
    string? Haunt,
    string? RunId,
    string? ShellId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Kind,
    JsonElement Payload,
    string? PreviousContentHash,
    string ContentHash)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ArchiveEvent(
    string Agent,
    string Kind,
    JsonElement Payload,
    string? Haunt = null,
    string? RunId = null,
    string? ShellId = null,
    string? EventId = null,
    DateTimeOffset? Timestamp = null);
