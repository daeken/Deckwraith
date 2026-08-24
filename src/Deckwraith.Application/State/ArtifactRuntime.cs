using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Application.State;

public sealed record ArtifactMutation(ArtifactReference Artifact, string CommitId);

public sealed class ArtifactRuntime
{
    private readonly IDeckStateStore _deckState;
    private readonly IArtifactStore _artifacts;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;

    public ArtifactRuntime(
        IDeckStateStore deckState,
        IArtifactStore artifacts,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        _deckState = deckState;
        _artifacts = artifacts;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<ArtifactMutation> StoreAsync(
        string wraith,
        string haunt,
        ReadOnlyMemory<byte> content,
        string? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedWraith = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var resolvedHaunt = await _deckState.ResolveHauntAsync(
            CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        var artifact = await _artifacts.PutAsync(
            resolvedHaunt, stream, mediaType, cancellationToken).ConfigureAwait(false);
        await _archive.AppendAsync(
            new ArchiveEvent(
                resolvedWraith.Value,
                "artifact.stored",
                CanonicalJson.ToElement(new
                {
                    artifact.Hash,
                    artifact.Length,
                    artifact.RelativePath,
                    artifact.MediaType,
                }),
                resolvedHaunt.Value,
                Timestamp: _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        var commit = await _checkpoints.CheckpointAsync(
            "artifact-stored", resolvedWraith, resolvedHaunt, cancellationToken)
            .ConfigureAwait(false);
        return new ArtifactMutation(artifact, commit);
    }

    public async Task<byte[]> ReadAsync(
        string haunt,
        string hash,
        CancellationToken cancellationToken = default)
    {
        var resolvedHaunt = await _deckState.ResolveHauntAsync(
            CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
        await using var stream = await _artifacts.OpenReadAsync(
            resolvedHaunt, hash, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
