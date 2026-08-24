using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Application.State;

public sealed record StateMutation<T>(T Value, string CommitId);

/// <summary>Coordinates coherent milestone-one mutations across files, archives, and Git.</summary>
public sealed class StateSpine : IDisposable
{
    private readonly IDeckStateStore _state;
    private readonly IAgentArchive _archive;
    private readonly IArtifactStore _artifacts;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateSpine(
        IDeckStateStore state,
        IAgentArchive archive,
        IArtifactStore artifacts,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        _state = state;
        _archive = archive;
        _artifacts = artifacts;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<string> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _checkpoints.InitializeRepositoryAsync(cancellationToken).ConfigureAwait(false);
            await _state.InitializeAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return await _checkpoints.CheckpointAsync(
                "deck-initialized", null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<StateMutation<IdentityDocument>> CreateWraithAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            var canonical = CanonicalName.Parse(name);
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var identity = await _state.CreateWraithAsync(
                canonical, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                Event(canonical, "wraith.created", new { name = canonical.Value }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "wraith-created", canonical, null, cancellationToken).ConfigureAwait(false);
            return new StateMutation<IdentityDocument>(identity, commit);
        }, cancellationToken);

    public Task<StateMutation<HauntDocument>> CreateHauntAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            var canonical = CanonicalName.Parse(name);
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var haunt = await _state.CreateHauntAsync(
                canonical, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "haunt-created", null, canonical, cancellationToken).ConfigureAwait(false);
            return new StateMutation<HauntDocument>(haunt, commit);
        }, cancellationToken);

    public Task<StateMutation<CanonicalName>> RenameWraithAsync(
        string source,
        string target,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolvedSource = await _state.ResolveWraithAsync(
                CanonicalName.Parse(source), cancellationToken).ConfigureAwait(false);
            var canonicalTarget = CanonicalName.Parse(target);
            var intent = await _state.RenameWraithAsync(
                resolvedSource, canonicalTarget, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                Event(canonicalTarget, "wraith.renamed", new
                {
                    previousName = resolvedSource.Value,
                    name = canonicalTarget.Value,
                }, eventId: intent.OperationId, timestamp: intent.CreatedAt),
                cancellationToken).ConfigureAwait(false);
            await _state.CompleteRenameAsync(
                intent.OperationId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "wraith-renamed", canonicalTarget, null, cancellationToken).ConfigureAwait(false);
            return new StateMutation<CanonicalName>(canonicalTarget, commit);
        }, cancellationToken);

    public Task<StateMutation<CanonicalName>> RenameHauntAsync(
        string source,
        string target,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolvedSource = await _state.ResolveHauntAsync(
                CanonicalName.Parse(source), cancellationToken).ConfigureAwait(false);
            var canonicalTarget = CanonicalName.Parse(target);
            var intent = await _state.RenameHauntAsync(
                resolvedSource, canonicalTarget, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await _state.CompleteRenameAsync(
                intent.OperationId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "haunt-renamed", null, canonicalTarget, cancellationToken).ConfigureAwait(false);
            return new StateMutation<CanonicalName>(canonicalTarget, commit);
        }, cancellationToken);

    public Task<StateMutation<ArtifactReference>> StoreArtifactAsync(
        string wraith,
        string haunt,
        Stream content,
        string? mediaType = null,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolvedWraith = await _state.ResolveWraithAsync(
                CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
            var resolvedHaunt = await _state.ResolveHauntAsync(
                CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
            var artifact = await _artifacts.PutAsync(
                resolvedHaunt, content, mediaType, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                Event(resolvedWraith, "artifact.stored", new
                {
                    artifact.Hash,
                    artifact.Length,
                    artifact.RelativePath,
                    artifact.MediaType,
                }, resolvedHaunt),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "artifact-stored", resolvedWraith, resolvedHaunt, cancellationToken).ConfigureAwait(false);
            return new StateMutation<ArtifactReference>(artifact, commit);
        }, cancellationToken);

    public Task<StateMutation<ArchiveRecord>> AppendEventAsync(
        string wraith,
        string kind,
        object payload,
        string? haunt = null,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(payload);
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolvedWraith = await _state.ResolveWraithAsync(
                CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
            CanonicalName? resolvedHaunt = haunt is null
                ? null
                : await _state.ResolveHauntAsync(
                    CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
            var record = await _archive.AppendAsync(
                Event(resolvedWraith, kind, payload, resolvedHaunt),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "archive-event-appended", resolvedWraith, resolvedHaunt, cancellationToken)
                .ConfigureAwait(false);
            return new StateMutation<ArchiveRecord>(record, commit);
        }, cancellationToken);

    public async Task<CanonicalName> ResolveWraithAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            return await _state.ResolveWraithAsync(
                CanonicalName.Parse(name), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CanonicalName> ResolveHauntAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            return await _state.ResolveHauntAsync(
                CanonicalName.Parse(name), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IdentityDocument> ReadIdentityAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWraithAsync(name, cancellationToken).ConfigureAwait(false);
        return await _state.ReadIdentityAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WraithDocument>> ListWraithsAsync(
        CancellationToken cancellationToken = default) =>
        _state.ListWraithsAsync(cancellationToken);

    public Task<IReadOnlyList<HauntDocument>> ListHauntsAsync(
        CancellationToken cancellationToken = default) =>
        _state.ListHauntsAsync(cancellationToken);

    public Task<StateMutation<IdentityDocument>> UpdateIdentityAsync(
        string wraith,
        IdentityDocument identity,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await _state.ResolveWraithAsync(
                CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
            var updated = await _state.WriteIdentityAsync(
                resolved, identity, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                Event(resolved, "identity.updated", new
                {
                    identityHash = CanonicalJson.Hash(updated),
                    updated.SchemaVersion,
                    updated.UpdatedAt,
                }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "identity-updated", resolved, null, cancellationToken).ConfigureAwait(false);
            return new StateMutation<IdentityDocument>(updated, commit);
        }, cancellationToken);

    public async Task<IReadOnlyList<ArchiveRecord>> ReadArchiveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWraithAsync(name, cancellationToken).ConfigureAwait(false);
        return await _archive.ReadAllAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private static ArchiveEvent Event(
        CanonicalName wraith,
        string kind,
        object payload,
        CanonicalName? haunt = null,
        string? eventId = null,
        DateTimeOffset? timestamp = null) =>
        new(
            wraith.Value,
            kind,
            JsonSerializer.SerializeToElement(payload),
            haunt?.Value,
            EventId: eventId,
            Timestamp: timestamp);

    private async Task RecoverIfNeededAsync(CancellationToken cancellationToken)
    {
        var recovered = await _state.RecoverPendingRenamesAsync(
            _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (var intent in recovered)
        {
            if (intent.Subject is RenameSubject.Wraith)
            {
                var target = CanonicalName.Parse(intent.Target);
                await _archive.AppendAsync(
                    Event(target, "wraith.renamed", new
                    {
                        previousName = intent.Source,
                        name = intent.Target,
                    }, eventId: intent.OperationId, timestamp: intent.CreatedAt),
                    cancellationToken).ConfigureAwait(false);
            }

            await _state.CompleteRenameAsync(
                intent.OperationId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        if (recovered.Count > 0)
        {
            await _checkpoints.CheckpointAsync(
                "rename-recovered", null, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T> WithMutationLockAsync<T>(
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await mutation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
