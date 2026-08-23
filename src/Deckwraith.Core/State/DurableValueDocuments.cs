using System.Text.Json;

namespace Deckwraith.Core.State;

public enum DurableValueScope
{
    Run,
    Agent,
    Haunt,
}

public sealed record DurableValueRecord(
    int SchemaVersion,
    DurableValueScope Scope,
    string Name,
    JsonElement Value,
    string ContentHash,
    string Writer,
    string? RunId,
    string? Haunt,
    long Version,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}
