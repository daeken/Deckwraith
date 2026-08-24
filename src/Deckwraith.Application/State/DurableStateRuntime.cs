using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Application.State;

public sealed record DurableStateMutation(DurableValueRecord? Value, string CommitId);

public sealed class DurableStateRuntime
{
    private readonly IDeckStateStore _deckState;
    private readonly IDurableValueStore _values;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;

    public DurableStateRuntime(
        IDeckStateStore deckState,
        IDurableValueStore values,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        _deckState = deckState;
        _values = values;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<DurableValueRecord?> GetAsync(
        string wraith,
        DurableValueScope scope,
        string name,
        string? runId = null,
        string? haunt = null,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(
            wraith, scope, runId, haunt, cancellationToken).ConfigureAwait(false);
        return await _values.ReadAsync(
            address.Wraith, scope, name, address.ScopeRunId, address.ScopeHaunt, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DurableValueRecord>> ListAsync(
        string wraith,
        DurableValueScope scope,
        string? runId = null,
        string? haunt = null,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(
            wraith, scope, runId, haunt, cancellationToken).ConfigureAwait(false);
        return await _values.ListAsync(
            address.Wraith, scope, address.ScopeRunId, address.ScopeHaunt, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DurableStateMutation> SetAsync(
        string wraith,
        DurableValueScope scope,
        string name,
        JsonElement value,
        string? runId = null,
        string? haunt = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(
            wraith, scope, runId, haunt, cancellationToken).ConfigureAwait(false);
        var record = await _values.WriteAsync(
            address.Wraith,
            scope,
            name,
            value,
            address.ScopeRunId,
            address.ScopeHaunt,
            expectedVersion,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await _archive.AppendAsync(
            Event(address, "state.value-written", new
            {
                record.Scope,
                record.Name,
                record.ContentHash,
                record.Version,
                expectedVersion,
            }),
            cancellationToken).ConfigureAwait(false);
        var commit = await _checkpoints.CheckpointAsync(
            "durable-state-written", address.Wraith, address.InvocationHaunt, cancellationToken)
            .ConfigureAwait(false);
        return new DurableStateMutation(record, commit);
    }

    public async Task<DurableStateMutation> RemoveAsync(
        string wraith,
        DurableValueScope scope,
        string name,
        string? runId = null,
        string? haunt = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(
            wraith, scope, runId, haunt, cancellationToken).ConfigureAwait(false);
        var record = await _values.RemoveAsync(
            address.Wraith,
            scope,
            name,
            address.ScopeRunId,
            address.ScopeHaunt,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new DurableStateMutation(null, string.Empty);
        }

        await _archive.AppendAsync(
            Event(address, "state.value-removed", new
            {
                record.Scope,
                record.Name,
                record.ContentHash,
                record.Version,
                expectedVersion,
            }),
            cancellationToken).ConfigureAwait(false);
        var commit = await _checkpoints.CheckpointAsync(
            "durable-state-removed", address.Wraith, address.InvocationHaunt, cancellationToken)
            .ConfigureAwait(false);
        return new DurableStateMutation(record, commit);
    }

    private async Task<ResolvedAddress> ResolveAsync(
        string wraith,
        DurableValueScope scope,
        string? runId,
        string? haunt,
        CancellationToken cancellationToken)
    {
        var resolvedWraith = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var resolvedHaunt = haunt is null
            ? (CanonicalName?)null
            : await _deckState.ResolveHauntAsync(
                CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
        if (scope is DurableValueScope.Run && string.IsNullOrWhiteSpace(runId))
        {
            throw new DeckStateException("Run-scoped state requires a run ID.");
        }

        if (scope is DurableValueScope.Haunt && resolvedHaunt is null)
        {
            throw new DeckStateException("Haunt-scoped state requires a haunt.");
        }

        return new ResolvedAddress(
            resolvedWraith,
            scope is DurableValueScope.Run ? runId : null,
            scope is DurableValueScope.Haunt ? resolvedHaunt : null,
            runId,
            resolvedHaunt);
    }

    private ArchiveEvent Event(ResolvedAddress address, string kind, object payload) => new(
        address.Wraith.Value,
        kind,
        CanonicalJson.ToElement(payload),
        address.InvocationHaunt?.Value,
        address.InvocationRunId,
        Timestamp: _clock.UtcNow);

    private sealed record ResolvedAddress(
        CanonicalName Wraith,
        string? ScopeRunId,
        CanonicalName? ScopeHaunt,
        string? InvocationRunId,
        CanonicalName? InvocationHaunt);
}
