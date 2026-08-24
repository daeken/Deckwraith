using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Application.State;

public sealed record StateMutation<T>(T Value, string CommitId);

public sealed record DeckInitialization(
    string CommitId,
    string SetupWraith,
    string SetupHaunt);

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

    public async Task<DeckInitialization> InitializeWithSetupAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _checkpoints.InitializeRepositoryAsync(cancellationToken).ConfigureAwait(false);
            await _state.InitializeAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);

            var requestedWraith = CanonicalName.Parse("steward");
            var requestedHaunt = CanonicalName.Parse("setup");
            var setupWraith = await _state.TryResolveWraithAsync(
                requestedWraith, cancellationToken).ConfigureAwait(false);
            if (setupWraith is null)
            {
                await _state.CreateWraithAsync(
                    requestedWraith, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                setupWraith = requestedWraith;
            }

            var setupHaunt = await _state.TryResolveHauntAsync(
                requestedHaunt, cancellationToken).ConfigureAwait(false);
            if (setupHaunt is null)
            {
                await _state.CreateHauntAsync(
                    requestedHaunt, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                setupHaunt = requestedHaunt;
            }

            var records = await _archive.ReadAllAsync(
                setupWraith.Value, cancellationToken).ConfigureAwait(false);
            if (!records.Any(record => record.Kind == "wraith.created"))
            {
                await _archive.AppendAsync(
                    Event(setupWraith.Value, "wraith.created", new
                    {
                        name = setupWraith.Value.Value,
                        invitedBy = "deck-initialization",
                    }),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!records.Any(record => record.Kind == "setup.invited"))
            {
                await _archive.AppendAsync(
                    Event(
                        setupWraith.Value,
                        "setup.invited",
                        new
                        {
                            haunt = setupHaunt.Value.Value,
                            purpose = "Collaborate on setup, tend the deck, and help adapt Deckwraith to the people using it.",
                            relationship = "collaborator",
                        },
                        setupHaunt.Value),
                    cancellationToken).ConfigureAwait(false);
            }

            var commit = await _checkpoints.CheckpointAsync(
                "deck-initialized-with-setup-collaborator",
                setupWraith.Value,
                setupHaunt.Value,
                cancellationToken).ConfigureAwait(false);
            return new DeckInitialization(
                commit,
                setupWraith.Value.Value,
                setupHaunt.Value.Value);
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

    public Task<StateMutation<HauntDocument>> ConfigureHauntProjectAsync(
        string haunt,
        string projectPath,
        bool autoCommitEnabled = false,
        ProjectCommitAuthor? author = null,
        IReadOnlyList<string>? allowedPaths = null,
        bool allowDirtyWorkingTree = false,
        CancellationToken cancellationToken = default) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await _state.ResolveHauntAsync(
                CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
            var project = NormalizeProjectPolicy(
                projectPath,
                autoCommitEnabled,
                author ?? ProjectCommitAuthor.ForWraith(),
                allowedPaths ?? ["."],
                allowDirtyWorkingTree);
            var updated = await _state.WriteHauntProjectAsync(
                resolved, project, cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "haunt-project-configured", null, resolved, cancellationToken).ConfigureAwait(false);
            return new StateMutation<HauntDocument>(updated, commit);
        }, cancellationToken);

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

    public Task<StateMutation<WraithDocument>> ArchiveWraithAsync(
        string wraith,
        CancellationToken cancellationToken = default) =>
        SetWraithArchivedAsync(wraith, archived: true, cancellationToken);

    public Task<StateMutation<WraithDocument>> RestoreWraithAsync(
        string wraith,
        CancellationToken cancellationToken = default) =>
        SetWraithArchivedAsync(wraith, archived: false, cancellationToken);

    public async Task<IReadOnlyList<ArchiveRecord>> ReadArchiveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWraithAsync(name, cancellationToken).ConfigureAwait(false);
        return await _archive.ReadAllAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private Task<StateMutation<WraithDocument>> SetWraithArchivedAsync(
        string wraith,
        bool archived,
        CancellationToken cancellationToken) =>
        WithMutationLockAsync(async () =>
        {
            await RecoverIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await _state.ResolveWraithAsync(
                CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
            var updated = await _state.SetWraithArchivedAsync(
                resolved, archived, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                Event(
                    resolved,
                    archived ? "wraith.archived" : "wraith.restored",
                    new { name = resolved.Value, updated.ArchivedAt }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                archived ? "wraith-archived" : "wraith-restored",
                resolved,
                null,
                cancellationToken).ConfigureAwait(false);
            return new StateMutation<WraithDocument>(updated, commit);
        }, cancellationToken);

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

    private static HauntProjectPolicy NormalizeProjectPolicy(
        string projectPath,
        bool autoCommitEnabled,
        ProjectCommitAuthor author,
        IReadOnlyList<string> allowedPaths,
        bool allowDirtyWorkingTree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(allowedPaths);
        var normalizedProject = Path.GetFullPath(projectPath);
        if (!Directory.Exists(normalizedProject))
        {
            throw new DeckStateException(
                $"Haunt project directory '{normalizedProject}' does not exist.");
        }

        var normalizedAuthor = author.Mode switch
        {
            ProjectCommitAuthorMode.Wraith => ProjectCommitAuthor.ForWraith(),
            ProjectCommitAuthorMode.Fixed => NormalizeFixedAuthor(author),
            _ => throw new DeckStateException(
                $"Project commit author mode '{author.Mode}' is not supported."),
        };
        if (allowedPaths.Count == 0)
        {
            throw new DeckStateException("A haunt project must allow at least one path scope.");
        }

        var scopes = allowedPaths
            .Select(scope => NormalizeAllowedPath(normalizedProject, scope))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new HauntProjectPolicy(
            normalizedProject,
            autoCommitEnabled,
            normalizedAuthor,
            scopes,
            allowDirtyWorkingTree);
    }

    private static ProjectCommitAuthor NormalizeFixedAuthor(ProjectCommitAuthor author)
    {
        if (string.IsNullOrWhiteSpace(author.Name) || string.IsNullOrWhiteSpace(author.Email))
        {
            throw new DeckStateException(
                "A fixed project commit author requires both a name and an email address.");
        }

        if (author.Name.IndexOfAny(['\r', '\n']) >= 0 ||
            author.Email.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new DeckStateException("Project commit author fields cannot contain newlines.");
        }

        return author with { Name = author.Name.Trim(), Email = author.Email.Trim() };
    }

    private static string NormalizeAllowedPath(string projectPath, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (Path.IsPathRooted(scope))
        {
            throw new DeckStateException(
                $"Allowed project path '{scope}' must be relative to the project directory.");
        }

        var resolved = Path.GetFullPath(Path.Combine(projectPath, scope));
        var relative = Path.GetRelativePath(projectPath, resolved);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new DeckStateException(
                $"Allowed project path '{scope}' escapes project directory '{projectPath}'.");
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

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
