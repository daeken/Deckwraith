using System.Text.Json;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Application.Abstractions;

public interface IDeckStateStore
{
    string RootPath { get; }

    Task InitializeAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<RenameIntent>> RecoverPendingRenamesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IdentityDocument> CreateWraithAsync(
        CanonicalName name,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<HauntDocument> CreateHauntAsync(
        CanonicalName name,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<CanonicalName> ResolveWraithAsync(CanonicalName name, CancellationToken cancellationToken);

    Task<CanonicalName> ResolveHauntAsync(CanonicalName name, CancellationToken cancellationToken);

    Task<IdentityDocument> ReadIdentityAsync(CanonicalName name, CancellationToken cancellationToken);

    Task<RenameIntent> RenameWraithAsync(
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<RenameIntent> RenameHauntAsync(
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task CompleteRenameAsync(
        string operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IAgentArchive
{
    Task<ArchiveRecord> AppendAsync(ArchiveEvent archiveEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveRecord>> ReadAllAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactReference> PutAsync(
        CanonicalName haunt,
        Stream content,
        string? mediaType,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        CanonicalName haunt,
        string hash,
        CancellationToken cancellationToken);
}

public interface ICheckpointStore
{
    Task InitializeRepositoryAsync(CancellationToken cancellationToken);

    Task<string> CheckpointAsync(
        string reason,
        CanonicalName? wraith,
        CanonicalName? haunt,
        CancellationToken cancellationToken);
}

public interface IDeckClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemDeckClock : IDeckClock
{
    public static SystemDeckClock Instance { get; } = new();

    private SystemDeckClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
