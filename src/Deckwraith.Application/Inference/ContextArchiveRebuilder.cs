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
        CurrentContextDocument context;
        var replayAfter = 0L;
        if (committed is null)
        {
            context = CurrentContextDocument.Create(
                wraith, identityHash, toolElisionTurns, DateTimeOffset.UnixEpoch);
        }
        else
        {
            if (!committed.Payload.TryGetProperty("context", out var contextElement))
            {
                throw new DeckStateException(
                    $"Context commit '{committed.EventId}' has no context payload.");
            }

            context = contextElement.Deserialize<CurrentContextDocument>(JsonOptions)
                ?? throw new DeckStateException(
                    $"Context commit '{committed.EventId}' contains JSON null.");
            if (!StringComparer.Ordinal.Equals(context.Agent, wraith.Value))
            {
                throw new DeckStateException(
                    $"Context commit '{committed.EventId}' belongs to '{context.Agent}', not '{wraith}'.");
            }

            context = context with
            {
                ArchiveFrontier = committed.Sequence,
                UpdatedAt = committed.Timestamp,
            };
            replayAfter = committed.Sequence;
        }

        var startedByOperation = records
            .Where(record => record.Payload.ValueKind is JsonValueKind.Object &&
                record.Payload.TryGetProperty("operationId", out var operationId) &&
                operationId.ValueKind is JsonValueKind.String &&
                IsStarted(record.Kind))
            .GroupBy(record => record.Payload.GetProperty("operationId").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var record in records.Where(record => record.Sequence > replayAfter))
        {
            context = record.Kind switch
            {
                "message.user" => ReplayMessage(context, record, ContextRole.User),
                "message.assistant" => ReplayMessage(context, record, ContextRole.Assistant),
                _ when IsToolTerminal(record.Kind) => ReplayTool(
                    context, record, startedByOperation),
                _ => context,
            };
        }

        return context with
        {
            IdentityHash = identityHash,
            ToolElisionTurns = toolElisionTurns,
        };
    }

    private static CurrentContextDocument ReplayMessage(
        CurrentContextDocument context,
        ArchiveRecord record,
        ContextRole role)
    {
        if (context.Items.Any(item => StringComparer.Ordinal.Equals(item.ItemId, record.EventId)) ||
            !record.Payload.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind is not JsonValueKind.String)
        {
            return context;
        }

        return context with
        {
            Revision = checked(context.Revision + 1),
            Turn = role is ContextRole.Assistant
                ? checked(context.Turn + 1)
                : context.Turn,
            ArchiveFrontier = record.Sequence,
            Items =
            [
                .. context.Items,
                ContextItem.Message(
                    record.EventId,
                    role,
                    textElement.GetString() ?? string.Empty,
                    record.Sequence),
            ],
            UpdatedAt = record.Timestamp,
        };
    }

    private static CurrentContextDocument ReplayTool(
        CurrentContextDocument context,
        ArchiveRecord terminal,
        Dictionary<string, ArchiveRecord> startedByOperation)
    {
        if (!terminal.Payload.TryGetProperty("operationId", out var operationIdElement) ||
            operationIdElement.ValueKind is not JsonValueKind.String)
        {
            return context;
        }

        var operationId = operationIdElement.GetString()!;
        if (context.Items.Any(item =>
                StringComparer.Ordinal.Equals(item.OperationId, operationId)) ||
            !startedByOperation.TryGetValue(operationId, out var started) ||
            started.Sequence > terminal.Sequence)
        {
            return context;
        }

        var callId = started.Payload.TryGetProperty("callId", out var callIdElement) &&
            callIdElement.ValueKind is JsonValueKind.String
            ? callIdElement.GetString()!
            : operationId;
        var tool = started.Payload.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind is JsonValueKind.String
            ? nameElement.GetString()!
            : "unknown-tool";
        var input = started.Payload.TryGetProperty("arguments", out var arguments)
            ? arguments.Clone()
            : JsonSerializer.SerializeToElement(new { });
        var output = terminal.Payload.TryGetProperty("output", out var outputElement)
            ? outputElement.Clone()
            : terminal.Payload.Clone();
        var status = terminal.Kind.EndsWith(".completed", StringComparison.Ordinal)
            ? OperationStatus.Completed
            : terminal.Kind.EndsWith(".failed", StringComparison.Ordinal)
                ? OperationStatus.Failed
                : terminal.Kind.EndsWith(".cancelled", StringComparison.Ordinal)
                    ? OperationStatus.Cancelled
                    : OperationStatus.OutcomeUnknown;
        return context with
        {
            Revision = checked(context.Revision + 1),
            ArchiveFrontier = terminal.Sequence,
            Items =
            [
                .. context.Items,
                ContextItem.ToolInteraction(
                    callId,
                    operationId,
                    tool,
                    status,
                    context.Turn,
                    input,
                    output,
                    started.Sequence,
                    terminal.Sequence),
            ],
            UpdatedAt = terminal.Timestamp,
        };
    }

    private static bool IsStarted(string kind) =>
        kind.EndsWith(".started", StringComparison.Ordinal) ||
        kind.EndsWith("-started", StringComparison.Ordinal);

    private static bool IsToolTerminal(string kind) =>
        kind.StartsWith("tool.", StringComparison.Ordinal) &&
        (kind.EndsWith(".completed", StringComparison.Ordinal) ||
         kind.EndsWith(".failed", StringComparison.Ordinal) ||
         kind.EndsWith(".cancelled", StringComparison.Ordinal) ||
         kind.EndsWith(".outcome-unknown", StringComparison.Ordinal));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
