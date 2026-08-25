using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Application.Inference;

public sealed record RunStartResult(RunDocument Run, CurrentContextDocument Context, string CommitId);

public sealed record ShellReplacementResult(
    RunDocument Run,
    ShellDocument PreviousShell,
    ShellDocument CurrentShell,
    string CommitId);

public sealed record RunEndResult(
    RunDocument Run,
    ShellDocument Shell,
    string CommitId);

public sealed record TurnResult(
    RunDocument Run,
    CurrentContextDocument Context,
    string Text,
    ModelFinishReason FinishReason,
    ModelUsageReported? Usage,
    string CommitId);

public interface IInferenceEventSink
{
    ValueTask OnModelRequestedAsync(
        string wraith,
        string runId,
        string shellId,
        string operationId,
        CancellationToken cancellationToken);

    ValueTask OnModelEventAsync(
        string wraith,
        string runId,
        string shellId,
        ModelEvent modelEvent,
        CancellationToken cancellationToken);
}

public sealed class InferenceRuntime : IDisposable
{
    private const int MaximumToolLoops = 16;

    private readonly IDeckStateStore _deckState;
    private readonly IInferenceStateStore _inferenceState;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IModelProviderRegistry _providers;
    private readonly IToolBroker _tools;
    private readonly IInferenceEventSink? _events;
    private readonly IDeckClock _clock;
    private readonly int _defaultToolElisionTurns;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentGates = new(StringComparer.Ordinal);

    public InferenceRuntime(
        IDeckStateStore deckState,
        IInferenceStateStore inferenceState,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IModelProviderRegistry providers,
        IToolBroker? tools = null,
        IDeckClock? clock = null,
        int defaultToolElisionTurns = 8,
        IInferenceEventSink? events = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(defaultToolElisionTurns);
        _deckState = deckState;
        _inferenceState = inferenceState;
        _archive = archive;
        _checkpoints = checkpoints;
        _providers = providers;
        _tools = tools ?? EmptyToolBroker.Instance;
        _events = events;
        _clock = clock ?? SystemDeckClock.Instance;
        _defaultToolElisionTurns = defaultToolElisionTurns;
    }

    public async Task<RunStartResult> StartRunAsync(
        string wraith,
        string? haunt,
        string objective,
        string providerId,
        string model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        await using var lifecycleLease = await _deckState.AcquireWraithLifecycleLeaseAsync(
            agent, cancellationToken).ConfigureAwait(false);
        var wraithDocument = await _deckState.ReadWraithAsync(agent, cancellationToken)
            .ConfigureAwait(false);
        if (wraithDocument.ArchivedAt is not null)
        {
            throw new DeckStateException(
                $"Wraith '{agent}' is archived and must be restored before starting a run.");
        }

        var gate = Gate(agent);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeRun = (await _inferenceState.ListRunsAsync(agent, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault(run =>
                    run.Status is not (RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed));
            if (activeRun is not null)
            {
                throw new DeckStateException(
                    $"Wraith '{agent}' already has active run '{activeRun.RunId}'.");
            }

            var resolvedHaunt = haunt is null
                ? (CanonicalName?)null
                : await _deckState.ResolveHauntAsync(
                    CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false);
            _ = _providers.GetProvider(providerId);
            var identity = await _deckState.ReadIdentityAsync(agent, cancellationToken).ConfigureAwait(false);
            var context = await _inferenceState.EnsureContextAsync(
                agent,
                CanonicalJson.Hash(identity),
                _defaultToolElisionTurns,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            var runId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
            var shellId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
            var shell = new ShellDocument(shellId, providerId, model, _clock.UtcNow, null, null);
            var run = new RunDocument(
                RunDocument.CurrentSchemaVersion,
                runId,
                agent.Value,
                resolvedHaunt?.Value,
                objective,
                RunStatus.Created,
                null,
                [shell],
                _clock.UtcNow,
                _clock.UtcNow);
            await _inferenceState.CreateRunAsync(agent, run, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "run.created", new
                {
                    run.Objective,
                    provider = providerId,
                    model,
                }),
                cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "shell.started", new
                {
                    shell.ShellId,
                    shell.Provider,
                    shell.Model,
                    shell.StartedAt,
                }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "run-created", agent, resolvedHaunt, cancellationToken).ConfigureAwait(false);
            return new RunStartResult(run, context, commit);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ShellReplacementResult> ReplaceShellAsync(
        string wraith,
        string runId,
        string providerId,
        string model,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var gate = Gate(agent);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _inferenceState.ReadRunAsync(agent, runId, cancellationToken)
                .ConfigureAwait(false);
            if (run.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed)
            {
                throw new DeckStateException(
                    $"Run '{runId}' is already terminal ({run.Status}).");
            }

            _ = _providers.GetProvider(providerId);
            var previous = run.Shells[^1];
            if (previous.EndedAt is not null)
            {
                throw new DeckStateException(
                    $"Run '{runId}' has no active shell to replace.");
            }

            var ended = previous with
            {
                EndedAt = _clock.UtcNow,
                EndReason = reason,
            };
            var current = new ShellDocument(
                Guid.CreateVersion7(_clock.UtcNow).ToString("N"),
                providerId,
                model,
                _clock.UtcNow,
                null,
                null);
            var shells = run.Shells.ToArray();
            shells[^1] = ended;
            run = run with
            {
                Shells = [.. shells, current],
                Status = RunStatus.AwaitingInput,
                StatusReason = "shell-replaced",
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteRunAsync(agent, run, cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                ArchiveEventFor(run, ended, "shell.ended", new
                {
                    ended.ShellId,
                    ended.Provider,
                    ended.Model,
                    ended.StartedAt,
                    ended.EndedAt,
                    ended.EndReason,
                }),
                cancellationToken).ConfigureAwait(false);
            await _archive.AppendAsync(
                ArchiveEventFor(run, current, "shell.started", new
                {
                    current.ShellId,
                    current.Provider,
                    current.Model,
                    current.StartedAt,
                    previousShellId = ended.ShellId,
                    reason,
                }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                "shell-replaced",
                agent,
                run.Haunt is null ? null : CanonicalName.Parse(run.Haunt),
                cancellationToken).ConfigureAwait(false);
            return new ShellReplacementResult(run, ended, current, commit);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<RunEndResult> CompleteRunAsync(
        string wraith,
        string runId,
        string reason,
        CancellationToken cancellationToken = default) =>
        EndRunAsync(wraith, runId, RunStatus.Completed, reason, cancellationToken);

    public Task<RunEndResult> CancelRunAsync(
        string wraith,
        string runId,
        string reason,
        CancellationToken cancellationToken = default) =>
        EndRunAsync(wraith, runId, RunStatus.Cancelled, reason, cancellationToken);

    public async Task<TurnResult> ExecuteTurnAsync(
        string wraith,
        string runId,
        string userText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var gate = Gate(agent);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _inferenceState.ReadRunAsync(agent, runId, cancellationToken)
                .ConfigureAwait(false);
            if (run.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed)
            {
                throw new DeckStateException($"Run '{runId}' is already terminal ({run.Status}).");
            }

            var shell = run.Shells[^1];
            var provider = _providers.GetProvider(shell.Provider);
            var identity = await _deckState.ReadIdentityAsync(agent, cancellationToken).ConfigureAwait(false);
            var context = await _inferenceState.EnsureContextAsync(
                agent,
                CanonicalJson.Hash(identity),
                _defaultToolElisionTurns,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            var elision = ToolElision.Apply(context, context.ToolElisionTurns, _clock.UtcNow);
            if (elision.Context != context)
            {
                var previousRevision = context.Revision;
                context = elision.Context;
                if (elision.ElidedOperationIds.Count > 0)
                {
                    var elisionRecord = await _archive.AppendAsync(
                        ArchiveEventFor(run, shell, "context.tools-elided", new
                        {
                            previousRevision,
                            context.Revision,
                            effectiveTurns = context.ToolElisionTurns,
                            operationIds = elision.ElidedOperationIds,
                        }),
                        cancellationToken).ConfigureAwait(false);
                    context = context with { ArchiveFrontier = elisionRecord.Sequence };
                }

                await _inferenceState.WriteContextAsync(
                    agent, context, previousRevision, cancellationToken).ConfigureAwait(false);
            }

            var userRecord = await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "message.user", new { text = userText }),
                cancellationToken).ConfigureAwait(false);
            context = context with
            {
                Revision = checked(context.Revision + 1),
                IdentityHash = CanonicalJson.Hash(identity),
                ArchiveFrontier = userRecord.Sequence,
                Items = [.. context.Items, ContextItem.Message(
                    userRecord.EventId,
                    ContextRole.User,
                    userText,
                    userRecord.Sequence)],
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteContextAsync(
                agent, context, context.Revision - 1, cancellationToken).ConfigureAwait(false);

            run = run with
            {
                Status = RunStatus.Running,
                StatusReason = null,
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteRunAsync(agent, run, cancellationToken).ConfigureAwait(false);

            try
            {
                var invocation = await InvokeUntilTerminalAsync(
                    agent, run, shell, provider, identity, context, cancellationToken)
                    .ConfigureAwait(false);
                var proposedContext = invocation.Context with
                {
                    Revision = checked(invocation.Context.Revision + 1),
                    Turn = checked(invocation.Context.Turn + 1),
                    UpdatedAt = _clock.UtcNow,
                };
                var contextCommitted = await _archive.AppendAsync(
                    ArchiveEventFor(run, shell, "context.committed", new
                    {
                        context = proposedContext,
                    }),
                    cancellationToken).ConfigureAwait(false);
                context = proposedContext with
                {
                    ArchiveFrontier = contextCommitted.Sequence,
                    UpdatedAt = contextCommitted.Timestamp,
                };
                await _inferenceState.WriteContextAsync(
                    agent, context, invocation.Context.Revision, cancellationToken).ConfigureAwait(false);
                run = run with
                {
                    Status = RunStatus.AwaitingInput,
                    StatusReason = "model-turn-completed",
                    UpdatedAt = _clock.UtcNow,
                };
                await _inferenceState.WriteRunAsync(agent, run, cancellationToken).ConfigureAwait(false);
                var commit = await _checkpoints.CheckpointAsync(
                    "model-turn-completed",
                    agent,
                    run.Haunt is null ? null : CanonicalName.Parse(run.Haunt),
                    cancellationToken).ConfigureAwait(false);
                return new TurnResult(
                    run,
                    context,
                    invocation.Text,
                    invocation.FinishReason,
                    invocation.Usage,
                    commit);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failedShell = shell with
                {
                    EndedAt = _clock.UtcNow,
                    EndReason = "run-failed",
                };
                var failedShells = run.Shells.ToArray();
                failedShells[^1] = failedShell;
                run = run with
                {
                    Status = RunStatus.Failed,
                    StatusReason = exception.Message,
                    Shells = failedShells,
                    UpdatedAt = _clock.UtcNow,
                };
                await _inferenceState.WriteRunAsync(agent, run, cancellationToken).ConfigureAwait(false);
                await _archive.AppendAsync(
                    ArchiveEventFor(run, failedShell, "shell.ended", new
                    {
                        failedShell.ShellId,
                        failedShell.Provider,
                        failedShell.Model,
                        failedShell.StartedAt,
                        failedShell.EndedAt,
                        failedShell.EndReason,
                    }),
                    cancellationToken).ConfigureAwait(false);
                await _archive.AppendAsync(
                    ArchiveEventFor(run, failedShell, "run.failed", new
                    {
                        error = exception.Message,
                        errorType = exception.GetType().FullName,
                    }),
                    cancellationToken).ConfigureAwait(false);
                await _checkpoints.CheckpointAsync(
                    "run-failed",
                    agent,
                    run.Haunt is null ? null : CanonicalName.Parse(run.Haunt),
                    cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cancelledShell = shell with
                {
                    EndedAt = _clock.UtcNow,
                    EndReason = "run-cancelled",
                };
                var cancelledShells = run.Shells.ToArray();
                cancelledShells[^1] = cancelledShell;
                run = run with
                {
                    Status = RunStatus.Cancelled,
                    StatusReason = "model-turn-cancelled",
                    Shells = cancelledShells,
                    UpdatedAt = _clock.UtcNow,
                };
                await _inferenceState.WriteRunAsync(
                    agent, run, CancellationToken.None).ConfigureAwait(false);
                await _archive.AppendAsync(
                    ArchiveEventFor(run, cancelledShell, "shell.ended", new
                    {
                        cancelledShell.ShellId,
                        cancelledShell.Provider,
                        cancelledShell.Model,
                        cancelledShell.StartedAt,
                        cancelledShell.EndedAt,
                        cancelledShell.EndReason,
                    }),
                    CancellationToken.None).ConfigureAwait(false);
                await _archive.AppendAsync(
                    ArchiveEventFor(run, cancelledShell, "run.cancelled", new
                    {
                        reason = run.StatusReason,
                    }),
                    CancellationToken.None).ConfigureAwait(false);
                await _checkpoints.CheckpointAsync(
                    "run-cancelled",
                    agent,
                    run.Haunt is null ? null : CanonicalName.Parse(run.Haunt),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _agentGates.Values)
        {
            gate.Dispose();
        }

        if (_tools is IDisposable disposableTools)
        {
            disposableTools.Dispose();
        }
    }

    private async Task<InvocationResult> InvokeUntilTerminalAsync(
        CanonicalName agent,
        RunDocument run,
        ShellDocument shell,
        IModelProvider provider,
        IdentityDocument identity,
        CurrentContextDocument initialContext,
        CancellationToken cancellationToken)
    {
        var context = initialContext;
        var accumulatedText = new StringBuilder();
        ModelUsageReported? totalUsage = null;
        for (var loop = 0; loop < MaximumToolLoops; loop++)
        {
            var toolDescriptors = _tools.Tools.Select(tool => new ContextToolDescriptor(
                tool.Name, CanonicalJson.Hash(tool.InputSchema)));
            var manifest = ContextManifestBuilder.Build(
                identity,
                context,
                run.Objective,
                provider.ProviderId,
                shell.Model,
                toolDescriptors);
            var requestId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
            var request = new ModelRequest(
                requestId,
                shell.Model,
                run.Objective,
                identity,
                context,
                manifest,
                _tools.Tools,
                null,
                null,
                null);
            await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "model.started", new
                {
                    operationId = requestId,
                    request,
                }, eventId: requestId),
                cancellationToken).ConfigureAwait(false);

            var toolCalls = new List<ModelToolCallCompleted>();
            ModelResponseCompleted? completed = null;
            var terminalEventPublished = false;
            try
            {
                if (_events is not null)
                {
                    await _events.OnModelRequestedAsync(
                        agent.Value,
                        run.RunId,
                        shell.ShellId,
                        requestId,
                        cancellationToken).ConfigureAwait(false);
                }

                await foreach (var modelEvent in provider.RunAsync(request, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (_events is not null)
                    {
                        await _events.OnModelEventAsync(
                            agent.Value,
                            run.RunId,
                            shell.ShellId,
                            modelEvent,
                            cancellationToken).ConfigureAwait(false);
                    }
                    terminalEventPublished |= modelEvent is
                        ModelResponseCompleted or ModelProviderError;

                    switch (modelEvent)
                    {
                        case ModelTextDelta delta:
                            accumulatedText.Append(delta.Delta);
                            break;
                        case ModelToolCallCompleted toolCall:
                            toolCalls.Add(toolCall);
                            break;
                        case ModelUsageReported usage:
                            totalUsage = AddUsage(totalUsage, usage);
                            break;
                        case ModelResponseCompleted responseCompleted:
                            completed = responseCompleted;
                            break;
                        case ModelProviderError error:
                            throw new ModelInvocationException(
                                error.Code, error.Message, error.Retryable);
                    }

                    if (completed is not null)
                    {
                        break;
                    }
                }

                if (completed is null)
                {
                    throw new ModelInvocationException(
                        "incomplete-stream",
                        "The provider stream ended without a terminal event.",
                        true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_events is not null && !terminalEventPublished)
                {
                    await _events.OnModelEventAsync(
                        agent.Value,
                        run.RunId,
                        shell.ShellId,
                        new ModelResponseCompleted(ModelFinishReason.Cancelled, null),
                        CancellationToken.None).ConfigureAwait(false);
                }
                await _archive.AppendAsync(
                    ArchiveEventFor(run, shell, "model.cancelled", new
                    {
                        operationId = requestId,
                    }),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                var failure = NormalizeProviderFailure(exception);
                if (_events is not null && !terminalEventPublished)
                {
                    await _events.OnModelEventAsync(
                        agent.Value,
                        run.RunId,
                        shell.ShellId,
                        new ModelProviderError(
                            failure.Code,
                            failure.Message,
                            failure.Retryable),
                        CancellationToken.None).ConfigureAwait(false);
                }
                await _archive.AppendAsync(
                    ArchiveEventFor(run, shell, "model.failed", new
                    {
                        operationId = requestId,
                        error = failure.Message,
                        errorType = exception.GetType().FullName,
                    }),
                    cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(failure, exception))
                {
                    throw;
                }
                throw failure;
            }

            var modelCompleted = await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "model.completed", new
                {
                    operationId = requestId,
                    completed.FinishReason,
                    completed.ContinuationId,
                    usage = totalUsage,
                }),
                cancellationToken).ConfigureAwait(false);
            context = context with { ArchiveFrontier = modelCompleted.Sequence };

            if (completed.FinishReason is ModelFinishReason.ToolCalls)
            {
                if (toolCalls.Count == 0)
                {
                    throw new ModelInvocationException(
                        "missing-tool-call", "Provider finished for tool calls without returning one.", false);
                }

                foreach (var toolCall in toolCalls)
                {
                    context = await ExecuteToolAsync(
                        agent, run, shell, context, toolCall, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var assistantRecord = await _archive.AppendAsync(
                ArchiveEventFor(run, shell, "message.assistant", new
                {
                    text = accumulatedText.ToString(),
                    completed.FinishReason,
                }),
                cancellationToken).ConfigureAwait(false);
            context = context with
            {
                Revision = checked(context.Revision + 1),
                ArchiveFrontier = assistantRecord.Sequence,
                Items = [.. context.Items, ContextItem.Message(
                    assistantRecord.EventId,
                    ContextRole.Assistant,
                    accumulatedText.ToString(),
                    assistantRecord.Sequence)],
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteContextAsync(
                agent, context, context.Revision - 1, cancellationToken).ConfigureAwait(false);
            return new InvocationResult(
                context, accumulatedText.ToString(), completed.FinishReason, totalUsage);
        }

        throw new ModelInvocationException(
            "tool-loop-limit", $"The provider exceeded {MaximumToolLoops} tool continuations.", false);
    }

    private static ModelInvocationException NormalizeProviderFailure(Exception exception) =>
        exception switch
        {
            ModelInvocationException invocation => invocation,
            TaskCanceledException cancelled when ContainsTimeout(cancelled) =>
                new ModelInvocationException(
                    "provider-timeout",
                    cancelled.Message,
                    true,
                    cancelled),
            _ => new ModelInvocationException(
                "provider-exception",
                exception.Message,
                true,
                exception),
        };

    private static bool ContainsTimeout(Exception exception)
    {
        for (var current = exception.InnerException; current is not null;
             current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }
        return false;
    }

    private async Task<CurrentContextDocument> ExecuteToolAsync(
        CanonicalName agent,
        RunDocument run,
        ShellDocument shell,
        CurrentContextDocument context,
        ModelToolCallCompleted toolCall,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
        var started = await _archive.AppendAsync(
            ArchiveEventFor(run, shell, "tool.started", new
            {
                operationId,
                toolCall.CallId,
                toolCall.Name,
                arguments = toolCall.Arguments,
            }, eventId: operationId),
            cancellationToken).ConfigureAwait(false);
        ToolExecutionResult result;
        try
        {
            result = await _tools.ExecuteAsync(
                toolCall.Name,
                toolCall.Arguments,
                new ToolExecutionContext(
                    agent.Value, run.Haunt, run.RunId, shell.ShellId, operationId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new ToolExecutionResult(
                OperationStatus.Failed,
                CanonicalJson.ToElement(new { error = exception.Message }),
                exception.Message);
        }
        catch (OperationCanceledException)
        {
            result = new ToolExecutionResult(
                OperationStatus.Cancelled,
                CanonicalJson.ToElement(new { cancelled = true }),
                "Tool execution was cancelled.");
        }

        var terminalCancellation = result.Status is OperationStatus.Cancelled
            ? CancellationToken.None
            : cancellationToken;
        var terminal = await _archive.AppendAsync(
            ArchiveEventFor(run, shell, $"tool.{result.Status.ToString().ToLowerInvariant()}", new
            {
                operationId,
                toolCall.CallId,
                toolCall.Name,
                result.Output,
                result.Error,
            }),
            terminalCancellation).ConfigureAwait(false);
        context = context with
        {
            Revision = checked(context.Revision + 1),
            ArchiveFrontier = terminal.Sequence,
            Items = [.. context.Items, ContextItem.ToolInteraction(
                toolCall.CallId,
                operationId,
                toolCall.Name,
                result.Status,
                context.Turn,
                toolCall.Arguments,
                result.Output,
                started.Sequence,
                terminal.Sequence)],
            UpdatedAt = _clock.UtcNow,
        };
        await _inferenceState.WriteContextAsync(
            agent, context, context.Revision - 1, terminalCancellation).ConfigureAwait(false);
        if (result.Status is OperationStatus.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return context;
    }

    private async Task<RunEndResult> EndRunAsync(
        string wraith,
        string runId,
        RunStatus status,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (status is not (RunStatus.Completed or RunStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var gate = Gate(agent);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _inferenceState.ReadRunAsync(agent, runId, cancellationToken)
                .ConfigureAwait(false);
            if (run.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed)
            {
                throw new DeckStateException(
                    $"Run '{runId}' is already terminal ({run.Status}).");
            }

            var active = run.Shells[^1];
            var ended = active.EndedAt is null
                ? active with { EndedAt = _clock.UtcNow, EndReason = reason }
                : active;
            var shells = run.Shells.ToArray();
            shells[^1] = ended;
            run = run with
            {
                Status = status,
                StatusReason = reason,
                Shells = shells,
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteRunAsync(agent, run, cancellationToken).ConfigureAwait(false);
            if (active.EndedAt is null)
            {
                await _archive.AppendAsync(
                    ArchiveEventFor(run, ended, "shell.ended", new
                    {
                        ended.ShellId,
                        ended.Provider,
                        ended.Model,
                        ended.StartedAt,
                        ended.EndedAt,
                        ended.EndReason,
                    }),
                    cancellationToken).ConfigureAwait(false);
            }

            await _archive.AppendAsync(
                ArchiveEventFor(
                    run,
                    ended,
                    status is RunStatus.Completed ? "run.completed" : "run.cancelled",
                    new { reason }),
                cancellationToken).ConfigureAwait(false);
            var commit = await _checkpoints.CheckpointAsync(
                status is RunStatus.Completed ? "run-completed" : "run-cancelled",
                agent,
                run.Haunt is null ? null : CanonicalName.Parse(run.Haunt),
                cancellationToken).ConfigureAwait(false);
            return new RunEndResult(run, ended, commit);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(CanonicalName agent) =>
        _agentGates.GetOrAdd(agent.Value, static _ => new SemaphoreSlim(1, 1));

    private ArchiveEvent ArchiveEventFor(
        RunDocument run,
        ShellDocument shell,
        string kind,
        object payload,
        string? eventId = null) =>
        new(
            run.Agent,
            kind,
            CanonicalJson.ToElement(payload),
            run.Haunt,
            run.RunId,
            shell.ShellId,
            eventId,
            _clock.UtcNow);

    private static ModelUsageReported AddUsage(
        ModelUsageReported? total,
        ModelUsageReported next) =>
        total is null
            ? next
            : new ModelUsageReported(
                checked(total.InputTokens + next.InputTokens),
                checked(total.OutputTokens + next.OutputTokens),
                total.CachedInputTokens is null && next.CachedInputTokens is null
                    ? null
                    : checked((total.CachedInputTokens ?? 0) + (next.CachedInputTokens ?? 0)));

    private sealed record InvocationResult(
        CurrentContextDocument Context,
        string Text,
        ModelFinishReason FinishReason,
        ModelUsageReported? Usage);
}

public sealed class ModelInvocationException : Exception
{
    public ModelInvocationException(
        string code,
        string message,
        bool retryable,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
