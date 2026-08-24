using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Continuity;

public sealed class CompactionRuntime
{
    public const string PromptVersion = "deckwraith-compaction-v1";

    private readonly IDeckStateStore _deckState;
    private readonly IInferenceStateStore _inferenceState;
    private readonly IAgentArchive _archive;
    private readonly ICompactionStore _compactions;
    private readonly ICheckpointStore _checkpoints;
    private readonly IModelProviderRegistry _providers;
    private readonly IDeckClock _clock;

    public CompactionRuntime(
        IDeckStateStore deckState,
        IInferenceStateStore inferenceState,
        IAgentArchive archive,
        ICompactionStore compactions,
        ICheckpointStore checkpoints,
        IModelProviderRegistry providers,
        IDeckClock? clock = null)
    {
        _deckState = deckState;
        _inferenceState = inferenceState;
        _archive = archive;
        _compactions = compactions;
        _checkpoints = checkpoints;
        _providers = providers;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<CompactionResult?> CompactAsync(
        string wraith,
        string providerId,
        string model,
        double fraction = 0.25,
        int minimumRecords = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var identity = await _deckState.ReadIdentityAsync(agent, cancellationToken)
            .ConfigureAwait(false);
        var context = await _inferenceState.ReadContextAsync(agent, cancellationToken)
            .ConfigureAwait(false);
        var records = await _archive.ReadAllAsync(agent, cancellationToken).ConfigureAwait(false);
        var existing = (await _compactions.ReadAllAsync(agent, cancellationToken)
            .ConfigureAwait(false)).Where(compaction => compaction.IsValid).ToArray();
        var selection = CompactionCoverage.SelectOldestPrefix(
            records, existing, fraction, minimumRecords);
        if (selection is null)
        {
            return null;
        }

        var source = records.Where(record =>
            record.Sequence >= selection.FirstSequence &&
            record.Sequence <= selection.LastSequence).ToArray();
        var provider = _providers.GetProvider(providerId);
        var compactionId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
        var parameters = CanonicalJson.ToElement(new { fraction, minimumRecords });
        await _archive.AppendAsync(
            new ArchiveEvent(
                agent.Value,
                "compaction.started",
                CanonicalJson.ToElement(new
                {
                    operationId = compactionId,
                    selection,
                    provider = providerId,
                    model,
                    promptVersion = PromptVersion,
                    parameters,
                }),
                EventId: compactionId,
                Timestamp: _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        try
        {
            var envelope = await InvokeCompactorAsync(
                provider,
                model,
                identity,
                source,
                selection,
                compactionId,
                cancellationToken).ConfigureAwait(false);
            var document = new CompactionDocument(
                CompactionDocument.CurrentSchemaVersion,
                compactionId,
                agent.Value,
                selection.FirstSequence,
                selection.LastSequence,
                selection.SourceContentHashes,
                existing.LastOrDefault()?.CompactionId,
                providerId,
                model,
                PromptVersion,
                parameters,
                envelope.Summary,
                envelope.UnresolvedItems,
                envelope.ArtifactReferences,
                _clock.UtcNow,
                true,
                null,
                null);
            ValidateSource(document, records);
            await _compactions.WriteAsync(agent, document, cancellationToken).ConfigureAwait(false);

            var compactedItems = context.Items.Where(item =>
                item.ArchiveLastSequence >= selection.FirstSequence &&
                item.ArchiveFirstSequence <= selection.LastSequence).ToArray();
            if (compactedItems.Any(item =>
                item.ArchiveFirstSequence < selection.FirstSequence ||
                item.ArchiveLastSequence > selection.LastSequence))
            {
                throw new DeckStateException(
                    "Compaction coverage splits a materialized context item.");
            }

            var preceding = context.Items.Where(item =>
                item.ArchiveLastSequence < selection.FirstSequence).ToArray();
            var following = context.Items.Where(item =>
                item.ArchiveFirstSequence > selection.LastSequence).ToArray();
            var accepted = await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "compaction.accepted",
                    CanonicalJson.ToElement(new
                    {
                        operationId = compactionId,
                        document,
                        replacedItemIds = compactedItems.Select(item => item.ItemId).ToArray(),
                    }),
                    EventId: Guid.CreateVersion7(_clock.UtcNow).ToString("N"),
                    Timestamp: _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
            var proposed = context with
            {
                Revision = checked(context.Revision + 1),
                ArchiveFrontier = accepted.Sequence,
                Items =
                [
                    .. preceding,
                    ContextItem.Compaction(
                        compactionId,
                        envelope.Summary,
                        selection.FirstSequence,
                        selection.LastSequence),
                    .. following,
                ],
                UpdatedAt = _clock.UtcNow,
            };
            var committed = await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "context.committed",
                    CanonicalJson.ToElement(new { context = proposed, cause = "compaction" }),
                    Timestamp: _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
            var materialized = proposed with
            {
                ArchiveFrontier = committed.Sequence,
                UpdatedAt = committed.Timestamp,
            };
            await _inferenceState.WriteContextAsync(
                agent, materialized, context.Revision, cancellationToken).ConfigureAwait(false);
            var acceptanceCommit = await _checkpoints.CheckpointAsync(
                "compaction-accepted", agent, null, cancellationToken).ConfigureAwait(false);
            document = document with { CheckpointCommit = acceptanceCommit };
            await _compactions.WriteAsync(agent, document, cancellationToken).ConfigureAwait(false);
            var provenanceCommit = await _checkpoints.CheckpointAsync(
                "compaction-provenance-recorded", agent, null, cancellationToken)
                .ConfigureAwait(false);
            return new CompactionResult(document, materialized, provenanceCommit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "compaction.failed",
                    CanonicalJson.ToElement(new
                    {
                        operationId = compactionId,
                        error = exception.Message,
                        errorType = exception.GetType().FullName,
                    }),
                    Timestamp: _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await _checkpoints.CheckpointAsync(
                "compaction-failed", agent, null, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static void ValidateSource(
        CompactionDocument compaction,
        IReadOnlyList<ArchiveRecord> records)
    {
        var source = records.Where(record =>
            record.Sequence >= compaction.FirstSequence &&
            record.Sequence <= compaction.LastSequence).ToArray();
        if (source.Length != compaction.SourceContentHashes.Count ||
            !source.Select(record => record.ContentHash)
                .SequenceEqual(compaction.SourceContentHashes, StringComparer.Ordinal))
        {
            throw new DeckStateException(
                $"Compaction '{compaction.CompactionId}' source validation failed.");
        }
    }

    private static async Task<CompactionEnvelope> InvokeCompactorAsync(
        IModelProvider provider,
        string model,
        IdentityDocument identity,
        IReadOnlyList<ArchiveRecord> source,
        CompactionSelection selection,
        string requestId,
        CancellationToken cancellationToken)
    {
        var sourceJson = Encoding.UTF8.GetString(CanonicalJson.Serialize(source));
        var preservationContract = $$"""
            Summarize the exact archive prefix {{selection.FirstSequence}}-{{selection.LastSequence}}.
            Preserve decisions, commitments, unresolved questions, user preferences, errors,
            artifact references, tool outcomes, and identity-relevant observations. Do not claim
            facts stronger than the source. Return exactly one JSON object and no Markdown:
            {"summary":"...","unresolvedItems":["..."],"artifactReferences":["..."]}

            Canonical source records:
            {{sourceJson}}
            """;
        var context = CurrentContextDocument.Create(
            CanonicalName.Parse(identity.Name), CanonicalJson.Hash(identity), 0, DateTimeOffset.UnixEpoch) with
        {
            Revision = 1,
            Items =
            [
                ContextItem.Message(
                    "compaction-source",
                    ContextRole.User,
                    preservationContract,
                    selection.LastSequence),
            ],
        };
        var manifest = ContextManifestBuilder.Build(
            identity,
            context,
            preservationContract,
            provider.ProviderId,
            model,
            []);
        var request = new ModelRequest(
            requestId,
            model,
            preservationContract,
            identity,
            context,
            manifest,
            [],
            null,
            null,
            null);
        var text = new StringBuilder();
        ModelResponseCompleted? completed = null;
        await foreach (var modelEvent in provider.RunAsync(request, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case ModelTextDelta delta:
                    text.Append(delta.Delta);
                    break;
                case ModelResponseCompleted response:
                    completed = response;
                    break;
                case ModelProviderError error:
                    throw new DeckStateException(
                        $"Compaction provider failed ({error.Code}): {error.Message}");
                case ModelToolCallCompleted:
                    throw new DeckStateException("Compaction providers may not call tools.");
            }
        }

        if (completed?.FinishReason is not ModelFinishReason.Stop)
        {
            throw new DeckStateException("Compaction provider did not return a complete summary.");
        }

        try
        {
            using var document = JsonDocument.Parse(text.ToString());
            var root = document.RootElement;
            var summary = root.GetProperty("summary").GetString();
            if (root.ValueKind is not JsonValueKind.Object ||
                root.EnumerateObject().Count() != 3 ||
                string.IsNullOrWhiteSpace(summary))
            {
                throw new JsonException("Compaction summary envelope has the wrong shape.");
            }

            return new CompactionEnvelope(
                summary,
                ReadStringArray(root, "unresolvedItems"),
                ReadStringArray(root, "artifactReferences"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DeckStateException(
                "Compaction provider returned an invalid structured summary.", exception);
        }
    }

    private static string[] ReadStringArray(JsonElement root, string property)
    {
        var element = root.GetProperty(property);
        if (element.ValueKind is not JsonValueKind.Array ||
            element.EnumerateArray().Any(item => item.ValueKind is not JsonValueKind.String))
        {
            throw new JsonException($"Compaction property '{property}' must be a string array.");
        }

        return element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private sealed record CompactionEnvelope(
        string Summary,
        IReadOnlyList<string> UnresolvedItems,
        IReadOnlyList<string> ArtifactReferences);
}
