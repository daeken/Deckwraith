using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Hosting;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Continuity;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.CSharp;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Mcp;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.Providers.Abstractions;
using Deckwraith.Providers.OpenAI;

namespace Deckwraith.Hosting;

public sealed record ProviderSnapshot(
    string ProviderId,
    ProviderCapabilities Capabilities,
    ProviderAuthenticationStatus? Authentication);

public sealed record DeckSnapshot(
    IReadOnlyList<WraithDocument> Wraiths,
    IReadOnlyList<HauntDocument> Haunts,
    IReadOnlyList<ProviderSnapshot> Providers,
    long EventCursor);

public sealed record DeckbookSummary(string Haunt, long Revision, int CellCount);

public sealed record WraithSnapshot(
    IdentityDocument Identity,
    CurrentContextDocument? Context,
    IReadOnlyList<RunDocument> Runs,
    IReadOnlyList<DeckbookSummary> Deckbooks,
    long EventCursor);

public sealed record ArchivePage(
    string Wraith,
    long AfterSequence,
    IReadOnlyList<Deckwraith.Core.Archives.ArchiveRecord> Records,
    bool HasMore);

public sealed record CheckpointSummary(
    string CommitId,
    IReadOnlyList<string> Parents,
    DateTimeOffset Timestamp,
    string Subject);

public sealed record ConversationAttachment(
    string FileName,
    string Hash,
    long Length,
    string? MediaType);

public sealed class DeckwraithHost : IDisposable
{
    public static readonly IReadOnlyList<string> Commands =
    [
        "deck.initialize",
        "wraith.create",
        "wraith.archive",
        "wraith.restore",
        "haunt.create",
        "haunt.configure-project",
        "identity.update",
        "run.start",
        "run.turn",
        "run.replace-shell",
        "run.complete",
        "run.cancel",
        "deckbook.insert",
        "deckbook.edit",
        "deckbook.delete",
        "deckbook.run-cell",
        "deckbook.run-remaining",
        "continuity.compact",
        "continuity.recover",
        "checkpoint.reverse",
    ];

    public static readonly IReadOnlyList<string> Queries =
    [
        "host.schema",
        "deck.snapshot",
        "wraith.snapshot",
        "deckbook.snapshot",
        "archive.snapshot",
        "checkpoint.snapshot",
    ];

    public static readonly IReadOnlyList<string> EventNames =
    [
        "host.request.started",
        "host.request.completed",
        "host.request.failed",
        "recovery.completed",
        "model.started",
        "model.text-delta",
        "model.tool-call",
        "model.usage",
        "model.completed",
        "model.error",
        "kernel.started",
        "kernel.value",
        "kernel.text",
        "kernel.error",
        "kernel.completed",
    ];

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _rootPath;
    private readonly JsonDeckStateStore _deckState;
    private readonly JsonInferenceStateStore _inferenceState;
    private readonly JsonlAgentArchive _archive;
    private readonly GitCheckpointStore _checkpoints;
    private readonly StateSpine _state;
    private readonly HostEventBuffer _events;
    private readonly ModelProviderRegistry _providers;
    private readonly PowerShellRuntimeManager _runspaces;
    private readonly PowerShellCellKernel _powerShellKernel;
    private readonly CSharpCellKernel _csharpKernel;
    private readonly DeckbookRuntime _deckbooks;
    private readonly InferenceRuntime _inference;
    private readonly CompactionRuntime _compaction;
    private readonly RecoveryRuntime _recovery;
    private readonly GitReversalRuntime _reversal;
    private readonly IDeckClock _clock;
    private readonly ConcurrentDictionary<string, RequestEntry> _requests = new(StringComparer.Ordinal);
    private bool _disposed;

    private DeckwraithHost(
        string rootPath,
        DeckwraithHostOptions options,
        IEnumerable<IModelProvider>? additionalProviders,
        IDeckClock? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.EventCapacity);
        _rootPath = Path.GetFullPath(rootPath);
        _clock = clock ?? SystemDeckClock.Instance;
        _deckState = new JsonDeckStateStore(_rootPath);
        _inferenceState = new JsonInferenceStateStore(_rootPath);
        _archive = new JsonlAgentArchive(_rootPath);
        _checkpoints = new GitCheckpointStore(_rootPath);
        _events = new HostEventBuffer(options.EventCapacity);
        var eventSink = new RuntimeEventSink(_events, _clock);
        _providers = options.CreateProviderRegistry(additionalProviders);
        var artifacts = new ArtifactRuntime(
            _deckState,
            new ContentAddressedArtifactStore(_rootPath),
            _archive,
            _checkpoints,
            _clock);
        var durableState = new DurableStateRuntime(
            _deckState,
            new JsonDurableValueStore(_rootPath),
            _archive,
            _checkpoints,
            _clock);
        _state = new StateSpine(
            _deckState,
            _archive,
            new ContentAddressedArtifactStore(_rootPath),
            _checkpoints,
            _clock);
        _runspaces = new PowerShellRuntimeManager(
            _rootPath,
            durableState,
            artifacts,
            _archive,
            _checkpoints,
            mcp: new McpCatalogRuntime(
                _rootPath, _deckState, _archive, _checkpoints, _clock),
            ownsMcp: true,
            clock: _clock,
            deckState: _deckState,
            projectCommitter: new GitProjectCommitter());
        _powerShellKernel = new PowerShellCellKernel(_runspaces);
        _csharpKernel = new CSharpCellKernel(
            durableState, artifacts, _archive, _checkpoints, _clock);
        _deckbooks = new DeckbookRuntime(
            _rootPath,
            _deckState,
            new CellKernelRegistry([_powerShellKernel, _csharpKernel]),
            _archive,
            _checkpoints,
            _clock,
            eventSink);
        _inference = new InferenceRuntime(
            _deckState,
            _inferenceState,
            _archive,
            _checkpoints,
            _providers,
            new PowerShellToolBroker(_runspaces),
            _clock,
            events: eventSink);
        _compaction = new CompactionRuntime(
            _deckState,
            _inferenceState,
            _archive,
            new JsonCompactionStore(_rootPath),
            _checkpoints,
            _providers,
            _clock);
        _recovery = new RecoveryRuntime(
            _rootPath,
            _deckState,
            _inferenceState,
            _archive,
            new JsonCompactionStore(_rootPath),
            _checkpoints,
            _clock);
        _reversal = new GitReversalRuntime(_rootPath, _checkpoints, _clock);
    }

    public long LatestEventCursor => _events.LatestCursor;

    public static async Task<DeckwraithHost> OpenAsync(
        string rootPath,
        DeckwraithHostOptions? options = null,
        IEnumerable<IModelProvider>? additionalProviders = null,
        IDeckClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        var host = new DeckwraithHost(
            rootPath,
            options ?? DeckwraithHostOptions.CreateDefault(),
            additionalProviders,
            clock);
        try
        {
            await host.RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public IAsyncEnumerable<HostEvent> ReadEventsAsync(
        long afterCursor,
        CancellationToken cancellationToken = default) =>
        _events.ReadAsync(afterCursor, cancellationToken);

    public async ValueTask<IReadOnlyList<ProviderSnapshot>> ReadProviderSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshots = new List<ProviderSnapshot>();
        foreach (var provider in _providers.Providers)
        {
            var authentication = provider is IProviderAuthenticationSource authenticationSource
                ? await authenticationSource.GetAuthenticationStatusAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;
            snapshots.Add(new ProviderSnapshot(
                provider.ProviderId,
                provider.Capabilities,
                authentication));
        }

        return snapshots;
    }

    public ValueTask<ProviderAuthenticationStatus> ImportOpenAiSubscriptionAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        GetOpenAiSubscriptionProvider().ImportCodexSessionAsync(path, cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> SignInOpenAiSubscriptionAsync(
        Func<Uri, CancellationToken, ValueTask> openBrowser,
        CancellationToken cancellationToken = default) =>
        GetOpenAiSubscriptionProvider().SignInWithBrowserAsync(
            openBrowser,
            cancellationToken: cancellationToken);

    public ValueTask DisconnectOpenAiSubscriptionAsync(
        CancellationToken cancellationToken = default) =>
        GetOpenAiSubscriptionProvider().DisconnectAsync(cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> SetProviderApiKeyAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        GetApiKeyProvider(providerId).SetApiKeyAsync(apiKey, cancellationToken);

    public ValueTask<ProviderAuthenticationStatus> DeleteStoredProviderApiKeyAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        GetApiKeyProvider(providerId).DeleteStoredApiKeyAsync(cancellationToken);

    public async Task<ConversationAttachment> StoreConversationAttachmentAsync(
        string wraith,
        string haunt,
        string path,
        string? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wraith);
        ArgumentException.ThrowIfNullOrWhiteSpace(haunt);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        const long maximumLength = 32L * 1024 * 1024;
        FileInfo file;
        try
        {
            file = new FileInfo(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HostProtocolException(
                "attachment-invalid", "The selected attachment path is invalid.");
        }

        if (!file.Exists)
        {
            throw new HostProtocolException(
                "attachment-missing", "The selected attachment no longer exists.");
        }

        if (file.Length > maximumLength)
        {
            throw new HostProtocolException(
                "attachment-too-large", "Conversation attachments may not exceed 32 MB each.");
        }

        await using var content = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stored = await _state.StoreArtifactAsync(
            wraith,
            haunt,
            content,
            mediaType,
            cancellationToken).ConfigureAwait(false);
        return new ConversationAttachment(
            file.Name,
            stored.Value.Hash,
            stored.Value.Length,
            stored.Value.MediaType);
    }

    public Task<HostResponse> ExecuteAsync(
        HostRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        HostProtocol.ValidateVersion(request.ProtocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        if (request.RequestId.Length > 128)
        {
            throw new HostProtocolException(
                "invalid-request-id", "Request IDs may not exceed 128 characters.");
        }

        var fingerprint = CanonicalJson.Hash(request);
        var entry = _requests.GetOrAdd(
            request.RequestId,
            _ => new RequestEntry(
                fingerprint,
                new Lazy<Task<HostResponse>>(
                    () => ExecuteCoreAsync(request, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication)));
        if (!StringComparer.Ordinal.Equals(entry.Fingerprint, fingerprint))
        {
            throw new HostProtocolException(
                "request-id-reused",
                $"Request ID '{request.RequestId}' was already used for a different envelope.");
        }

        return entry.Response.Value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inference.Dispose();
        _deckbooks.Dispose();
        _csharpKernel.Dispose();
        _powerShellKernel.Dispose();
        _runspaces.Dispose();
        _state.Dispose();
        _events.Dispose();
    }

    private async Task RecoverOnStartupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(_rootPath, "deck.json")))
        {
            return;
        }

        foreach (var wraith in await _deckState.ListWraithsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var result = await _recovery.RecoverAsync(wraith.Name, cancellationToken)
                .ConfigureAwait(false);
            if (result.Incident is not null)
            {
                _events.Publish(
                    "recovery.completed",
                    new { wraith = wraith.Name, result.Incident },
                    _clock.UtcNow);
            }
        }
    }

    private async Task<HostResponse> ExecuteCoreAsync(
        HostRequest request,
        CancellationToken cancellationToken)
    {
        _events.Publish(
            "host.request.started",
            new { request.RequestId, request.Kind, request.Name },
            _clock.UtcNow);
        try
        {
            var result = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            var completed = _events.Publish(
                "host.request.completed",
                new { request.RequestId, request.Kind, request.Name },
                _clock.UtcNow);
            return HostResponse.Completed(request, result, completed.Cursor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = MapError(exception);
            var failed = _events.Publish(
                "host.request.failed",
                new
                {
                    request.RequestId,
                    request.Kind,
                    request.Name,
                    error.Code,
                    error.Message,
                },
                _clock.UtcNow);
            return HostResponse.Failed(request, error, failed.Cursor);
        }
    }

    private Task<object?> DispatchAsync(HostRequest request, CancellationToken cancellationToken)
    {
        var known = request.Kind is HostRequestKind.Command ? Commands : Queries;
        if (!known.Contains(request.Name, StringComparer.Ordinal))
        {
            throw new HostProtocolException(
                "unknown-request",
                $"Unknown {request.Kind.ToString().ToLowerInvariant()} '{request.Name}'.");
        }

        return request.Kind switch
        {
            HostRequestKind.Command => DispatchCommandAsync(request.Name, request.Payload, cancellationToken),
            HostRequestKind.Query => DispatchQueryAsync(request.Name, request.Payload, cancellationToken),
            _ => throw new HostProtocolException("invalid-request-kind", "Unknown request kind."),
        };
    }

    private async Task<object?> DispatchCommandAsync(
        string name,
        JsonElement payload,
        CancellationToken cancellationToken) => name switch
    {
        "deck.initialize" => await InitializeDeckAsync(cancellationToken).ConfigureAwait(false),
        "wraith.create" => await _state.CreateWraithAsync(
            Read<CreateNamePayload>(payload).Name, cancellationToken).ConfigureAwait(false),
        "wraith.archive" => await _state.ArchiveWraithAsync(
            Read<WraithPayload>(payload).Wraith, cancellationToken).ConfigureAwait(false),
        "wraith.restore" => await _state.RestoreWraithAsync(
            Read<WraithPayload>(payload).Wraith, cancellationToken).ConfigureAwait(false),
        "haunt.create" => await _state.CreateHauntAsync(
            Read<CreateNamePayload>(payload).Name, cancellationToken).ConfigureAwait(false),
        "haunt.configure-project" => await ConfigureHauntProjectAsync(
            payload, cancellationToken).ConfigureAwait(false),
        "identity.update" => await UpdateIdentityAsync(payload, cancellationToken).ConfigureAwait(false),
        "run.start" => await StartRunAsync(payload, cancellationToken).ConfigureAwait(false),
        "run.turn" => await ExecuteTurnAsync(payload, cancellationToken).ConfigureAwait(false),
        "run.replace-shell" => await ReplaceShellAsync(payload, cancellationToken).ConfigureAwait(false),
        "run.complete" => await CompleteRunAsync(payload, cancellationToken).ConfigureAwait(false),
        "run.cancel" => await CancelRunAsync(payload, cancellationToken).ConfigureAwait(false),
        "deckbook.insert" => await InsertCellAsync(payload, cancellationToken).ConfigureAwait(false),
        "deckbook.edit" => await EditCellAsync(payload, cancellationToken).ConfigureAwait(false),
        "deckbook.delete" => await DeleteCellAsync(payload, cancellationToken).ConfigureAwait(false),
        "deckbook.run-cell" => await RunCellAsync(payload, cancellationToken).ConfigureAwait(false),
        "deckbook.run-remaining" => await RunRemainingAsync(payload, cancellationToken).ConfigureAwait(false),
        "continuity.compact" => await CompactAsync(payload, cancellationToken).ConfigureAwait(false),
        "continuity.recover" => await _recovery.RecoverAsync(
            Read<WraithPayload>(payload).Wraith, cancellationToken).ConfigureAwait(false),
        "checkpoint.reverse" => await _reversal.ReverseCommitAsync(
            Read<ReversePayload>(payload).Commit, cancellationToken).ConfigureAwait(false),
        _ => throw new UnreachableException(),
    };

    private async Task<object> InitializeDeckAsync(CancellationToken cancellationToken)
    {
        var initialized = await _state.InitializeWithSetupAsync(cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            rootPath = _rootPath,
            commitId = initialized.CommitId,
            setupWraith = initialized.SetupWraith,
            setupHaunt = initialized.SetupHaunt,
        };
    }

    private async Task<object?> DispatchQueryAsync(
        string name,
        JsonElement payload,
        CancellationToken cancellationToken) => name switch
    {
        "host.schema" => new HostSchemaDescriptor(
            HostProtocol.CurrentVersion, Commands, Queries, EventNames),
        "deck.snapshot" => await ReadDeckSnapshotAsync(cancellationToken).ConfigureAwait(false),
        "wraith.snapshot" => await ReadWraithSnapshotAsync(payload, cancellationToken)
            .ConfigureAwait(false),
        "deckbook.snapshot" => await ReadDeckbookSnapshotAsync(payload, cancellationToken)
            .ConfigureAwait(false),
        "archive.snapshot" => await ReadArchiveSnapshotAsync(payload, cancellationToken)
            .ConfigureAwait(false),
        "checkpoint.snapshot" => await ReadCheckpointsAsync(payload, cancellationToken)
            .ConfigureAwait(false),
        _ => throw new UnreachableException(),
    };

    private async Task<object> UpdateIdentityAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Read<UpdateIdentityPayload>(payload);
        return await _state.UpdateIdentityAsync(
            command.Wraith, command.Identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ConfigureHauntProjectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Read<ConfigureHauntProjectPayload>(payload);
        return await _state.ConfigureHauntProjectAsync(
            command.Haunt,
            command.ProjectPath,
            command.AutoCommitEnabled,
            command.Author,
            command.AllowedPaths,
            command.AllowDirtyWorkingTree,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> StartRunAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<StartRunPayload>(payload);
        return await _inference.StartRunAsync(
            command.Wraith,
            EmptyToNull(command.Haunt),
            command.Objective,
            command.Provider,
            command.Model,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ExecuteTurnAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<TurnPayload>(payload);
        return await _inference.ExecuteTurnAsync(
            command.Wraith, command.RunId, command.Message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ReplaceShellAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<ReplaceShellPayload>(payload);
        return await _inference.ReplaceShellAsync(
            command.Wraith,
            command.RunId,
            command.Provider,
            command.Model,
            command.Reason,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> CompleteRunAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<EndRunPayload>(payload);
        return await _inference.CompleteRunAsync(
            command.Wraith, command.RunId, command.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> CancelRunAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<EndRunPayload>(payload);
        return await _inference.CancelRunAsync(
            command.Wraith, command.RunId, command.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> InsertCellAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<InsertCellPayload>(payload);
        return await _deckbooks.InsertAsync(
            command.Wraith,
            command.Haunt,
            new InsertDeckbookCell(
                command.Name,
                command.Kind,
                command.Source,
                command.Kernel,
                command.ContextPolicy,
                command.Synopsis,
                command.Before,
                command.After),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> EditCellAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<EditCellPayload>(payload);
        return await _deckbooks.EditAsync(
            command.Wraith,
            command.Haunt,
            command.Name,
            command.Source,
            command.Kind,
            command.Kernel,
            command.Synopsis,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DeleteCellAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<CellPayload>(payload);
        return await _deckbooks.DeleteAsync(
            command.Wraith, command.Haunt, command.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> RunCellAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<RunCellPayload>(payload);
        return await _deckbooks.RunCellAsync(
            command.Wraith,
            command.Haunt,
            command.Name,
            EmptyToNull(command.RunId),
            command.Input,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> RunRemainingAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<RunRemainingPayload>(payload);
        return await _deckbooks.RunRemainingAsync(
            command.Wraith,
            command.Haunt,
            command.From,
            EmptyToNull(command.RunId),
            command.Input,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> CompactAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var command = Read<CompactPayload>(payload);
        return await _compaction.CompactAsync(
            command.Wraith,
            command.Provider,
            command.Model,
            command.Fraction,
            command.MinimumRecords,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeckSnapshot> ReadDeckSnapshotAsync(CancellationToken cancellationToken) =>
        new(
            await _state.ListWraithsAsync(cancellationToken).ConfigureAwait(false),
            await _state.ListHauntsAsync(cancellationToken).ConfigureAwait(false),
            _providers.Providers.Select(provider => new ProviderSnapshot(
                provider.ProviderId,
                provider.Capabilities,
                Authentication: null)).ToArray(),
            _events.LatestCursor);

    private OpenAiSubscriptionProvider GetOpenAiSubscriptionProvider() =>
        _providers.GetProvider(OpenAiSubscriptionProvider.Id) as OpenAiSubscriptionProvider ??
        throw new HostProtocolException(
            "provider-unavailable", "OpenAI subscription access is not configured in this host.");

    private IProviderApiKeyAuthenticationSource GetApiKeyProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        IModelProvider provider;
        try
        {
            provider = _providers.GetProvider(providerId);
        }
        catch (KeyNotFoundException)
        {
            throw new HostProtocolException(
                "provider-unavailable",
                $"Provider '{providerId}' is not configured in this host.");
        }

        return provider as IProviderApiKeyAuthenticationSource ??
            throw new HostProtocolException(
                "provider-access-unsupported",
                $"Provider '{providerId}' does not accept a stored API key.");
    }

    private async Task<WraithSnapshot> ReadWraithSnapshotAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var command = Read<WraithPayload>(payload);
        var resolved = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(command.Wraith), cancellationToken).ConfigureAwait(false);
        var identity = await _deckState.ReadIdentityAsync(resolved, cancellationToken)
            .ConfigureAwait(false);
        var contextPath = Path.Combine(_rootPath, "agents", resolved.Value, "context.json");
        var context = File.Exists(contextPath)
            ? await _inferenceState.ReadContextAsync(resolved, cancellationToken).ConfigureAwait(false)
            : null;
        var runs = await _inferenceState.ListRunsAsync(resolved, cancellationToken).ConfigureAwait(false);
        var deckbooks = new List<DeckbookSummary>();
        foreach (var haunt in await _state.ListHauntsAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = await _deckbooks.GetAsync(
                resolved.Value, haunt.Name, cancellationToken).ConfigureAwait(false);
            if (snapshot.Deckbook.Revision > 0 || snapshot.Cells.Count > 0)
            {
                deckbooks.Add(new DeckbookSummary(
                    haunt.Name, snapshot.Deckbook.Revision, snapshot.Cells.Count));
            }
        }

        return new WraithSnapshot(identity, context, runs, deckbooks, _events.LatestCursor);
    }

    private async Task<object> ReadDeckbookSnapshotAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Read<DeckbookPayload>(payload);
        return await _deckbooks.GetAsync(
            query.Wraith, query.Haunt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArchivePage> ReadArchiveSnapshotAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Read<ArchivePayload>(payload);
        if (query.Limit is < 1 or > 1000)
        {
            throw new HostProtocolException("invalid-limit", "Archive limits must be between 1 and 1000.");
        }

        var resolved = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(query.Wraith), cancellationToken).ConfigureAwait(false);
        var records = await _archive.ReadAllAsync(resolved, cancellationToken).ConfigureAwait(false);
        var remaining = records.Where(record => record.Sequence > query.AfterSequence).ToArray();
        return new ArchivePage(
            resolved.Value,
            query.AfterSequence,
            remaining.Take(query.Limit).ToArray(),
            remaining.Length > query.Limit);
    }

    private async Task<object> ReadCheckpointsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Read<CheckpointPayload>(payload);
        if (query.Limit is < 1 or > 500)
        {
            throw new HostProtocolException(
                "invalid-limit", "Checkpoint limits must be between 1 and 500.");
        }

        return await GitHistoryInspector.ReadAsync(
            _rootPath, query.Limit, cancellationToken).ConfigureAwait(false);
    }

    private static T Read<T>(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            payload = JsonSerializer.SerializeToElement(new { });
        }

        return payload.Deserialize<T>(JsonOptions) ??
            throw new HostProtocolException("invalid-payload", "Request payload was JSON null.");
    }

    private static HostProtocolError MapError(Exception exception) => exception switch
    {
        HostProtocolException protocol => new(protocol.Code, protocol.Message, false),
        JsonException json => new("invalid-payload", json.Message, false),
        DeckStateException state => new("state-conflict", state.Message, false),
        KeyNotFoundException missing => new("not-found", missing.Message, false),
        ArgumentException argument => new("invalid-argument", argument.Message, false),
        _ => new("host-error", exception.Message, false),
    };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record RequestEntry(string Fingerprint, Lazy<Task<HostResponse>> Response);

    private sealed record CreateNamePayload(string Name);

    private sealed record WraithPayload(string Wraith);

    private sealed record ConfigureHauntProjectPayload(
        string Haunt,
        string ProjectPath,
        bool AutoCommitEnabled = false,
        ProjectCommitAuthor? Author = null,
        IReadOnlyList<string>? AllowedPaths = null,
        bool AllowDirtyWorkingTree = false);

    private sealed record DeckbookPayload(string Wraith, string Haunt);

    private sealed record UpdateIdentityPayload(string Wraith, IdentityDocument Identity);

    private sealed record StartRunPayload(
        string Wraith,
        string? Haunt,
        string Objective,
        string Provider,
        string Model);

    private sealed record TurnPayload(string Wraith, string RunId, string Message);

    private sealed record ReplaceShellPayload(
        string Wraith,
        string RunId,
        string Provider,
        string Model,
        string Reason);

    private sealed record EndRunPayload(string Wraith, string RunId, string Reason);

    private sealed record InsertCellPayload(
        string Wraith,
        string Haunt,
        string Name,
        DeckbookCellKind Kind,
        string Source,
        string? Kernel = null,
        CellContextPolicy ContextPolicy = CellContextPolicy.WhenRelevant,
        string? Synopsis = null,
        string? Before = null,
        string? After = null);

    private sealed record EditCellPayload(
        string Wraith,
        string Haunt,
        string Name,
        string Source,
        DeckbookCellKind? Kind = null,
        string? Kernel = null,
        string? Synopsis = null);

    private sealed record CellPayload(string Wraith, string Haunt, string Name);

    private sealed record RunCellPayload(
        string Wraith,
        string Haunt,
        string Name,
        string? RunId,
        JsonElement Input);

    private sealed record RunRemainingPayload(
        string Wraith,
        string Haunt,
        string From,
        string? RunId,
        JsonElement Input);

    private sealed record CompactPayload(
        string Wraith,
        string Provider,
        string Model,
        double Fraction = 0.25,
        int MinimumRecords = 8);

    private sealed record ReversePayload(string Commit);

    private sealed record ArchivePayload(
        string Wraith,
        long AfterSequence = 0,
        int Limit = 250);

    private sealed record CheckpointPayload(int Limit = 100);

    private sealed class RuntimeEventSink(HostEventBuffer events, IDeckClock clock) :
        IInferenceEventSink,
        IDeckbookEventSink
    {
        public ValueTask OnModelEventAsync(
            string wraith,
            string runId,
            string shellId,
            ModelEvent modelEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, payload) = modelEvent switch
            {
                ModelResponseStarted started => (
                    "model.started",
                    (object)new { wraith, runId, shellId, started.ProviderRequestId }),
                ModelTextDelta delta => (
                    "model.text-delta",
                    new { wraith, runId, shellId, delta.Delta }),
                ModelToolCallCompleted call => (
                    "model.tool-call",
                    new { wraith, runId, shellId, call.CallId, call.Name, call.Arguments }),
                ModelUsageReported usage => (
                    "model.usage",
                    new
                    {
                        wraith,
                        runId,
                        shellId,
                        usage.InputTokens,
                        usage.OutputTokens,
                        usage.CachedInputTokens,
                    }),
                ModelResponseCompleted completed => (
                    "model.completed",
                    new
                    {
                        wraith,
                        runId,
                        shellId,
                        completed.FinishReason,
                        completed.ContinuationId,
                    }),
                ModelProviderError error => (
                    "model.error",
                    new { wraith, runId, shellId, error.Code, error.Message, error.Retryable }),
                _ => throw new UnreachableException(),
            };
            events.Publish(name, payload, clock.UtcNow);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnKernelEventAsync(
            CellExecutionRequest request,
            CellKernelEvent kernelEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, payload) = kernelEvent switch
            {
                CellKernelStarted started => (
                    "kernel.started",
                    (object)new
                    {
                        request.ExecutionId,
                        request.Wraith,
                        request.RunId,
                        request.Haunt,
                        request.CellName,
                        started.KernelVersion,
                        started.KernelEpoch,
                    }),
                CellKernelValueProduced value => (
                    "kernel.value",
                    new
                    {
                        request.ExecutionId,
                        request.Wraith,
                        request.Haunt,
                        request.CellName,
                        value.Value,
                    }),
                CellKernelTextProduced text => (
                    "kernel.text",
                    new
                    {
                        request.ExecutionId,
                        request.Wraith,
                        request.Haunt,
                        request.CellName,
                        text.Stream,
                        text.Text,
                    }),
                CellKernelErrorProduced error => (
                    "kernel.error",
                    new
                    {
                        request.ExecutionId,
                        request.Wraith,
                        request.Haunt,
                        request.CellName,
                        error.ErrorId,
                        error.Message,
                    }),
                CellKernelCompleted completed => (
                    "kernel.completed",
                    new
                    {
                        request.ExecutionId,
                        request.Wraith,
                        request.Haunt,
                        request.CellName,
                        completed.Status,
                    }),
                _ => throw new UnreachableException(),
            };
            events.Publish(name, payload, clock.UtcNow);
            return ValueTask.CompletedTask;
        }
    }
}

internal static class GitHistoryInspector
{
    public static async Task<IReadOnlyList<CheckpointSummary>> ReadAsync(
        string rootPath,
        int limit,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = rootPath,
        };
        foreach (var argument in new[]
        {
            "log",
            $"--max-count={limit}",
            "--format=%H%x1f%P%x1f%cI%x1f%s%x1e",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Git checkpoint inspector.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new DeckStateException(
                $"Git checkpoint inspection failed: {(await error.ConfigureAwait(false)).Trim()}");
        }

        return (await output.ConfigureAwait(false))
            .Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(record => record.Split('\x1f'))
            .Where(fields => fields.Length == 4)
            .Select(fields => new CheckpointSummary(
                fields[0],
                fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                DateTimeOffset.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                fields[3]))
            .ToArray();
    }
}
