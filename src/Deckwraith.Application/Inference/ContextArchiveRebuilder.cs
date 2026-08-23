using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Application.Inference;

public static class ContextArchiveRebuilder
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static CurrentContextDocument Rebuild(
        CanonicalName wraith,
        IReadOnlyList<ArchiveRecord> records,
        string identityHash,
        int toolElisionTurns,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityHash);
        ArgumentOutOfRangeException.ThrowIfNegative(toolElisionTurns);
        var committed = records.LastOrDefault(record =>
            StringComparer.Ordinal.Equals(record.Agent, wraith.Value) &&
            StringComparer.Ordinal.Equals(record.Kind, "context.committed"));
        if (committed is null)
        {
            return CurrentContextDocument.Create(
                wraith, identityHash, toolElisionTurns, now);
        }

        if (!committed.Payload.TryGetProperty("context", out var contextElement))
        {
            throw new DeckStateException(
                $"Context commit '{committed.EventId}' has no context payload.");
        }

        var context = contextElement.Deserialize<CurrentContextDocument>(JsonOptions)
            ?? throw new DeckStateException(
                $"Context commit '{committed.EventId}' contains JSON null.");
        if (!StringComparer.Ordinal.Equals(context.Agent, wraith.Value))
        {
            throw new DeckStateException(
                $"Context commit '{committed.EventId}' belongs to '{context.Agent}', not '{wraith}'.");
        }

        return context with
        {
            ArchiveFrontier = committed.Sequence,
            UpdatedAt = committed.Timestamp,
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
