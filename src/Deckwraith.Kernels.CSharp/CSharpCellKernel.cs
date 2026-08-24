using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Deckwraith.Kernels.CSharp;

public sealed record CSharpRuntimeInfo(
    string Wraith,
    long Epoch,
    DateTimeOffset StartedAt,
    bool VolatileStateLost,
    string RuntimeVersion);

public sealed class CSharpCellKernel : ICellKernel, IDisposable
{
    private static readonly ScriptOptions Options = CreateOptions();
    private readonly DurableStateRuntime _state;
    private readonly ArtifactRuntime _artifacts;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;
    private readonly ConcurrentDictionary<string, CSharpSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _executions =
        new(StringComparer.Ordinal);

    public CSharpCellKernel(
        DurableStateRuntime state,
        ArtifactRuntime artifacts,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        _state = state;
        _artifacts = artifacts;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public string KernelId => "csharp";

    public KernelCapabilities Capabilities { get; } = new(
        Streaming: false,
        StructuredValues: true,
        Interruption: true,
        AmbientState: true);

    public async IAsyncEnumerable<CellKernelEvent> ExecuteAsync(
        CellExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = GetOrCreateSession(request.Wraith);
        var runtime = session.Info;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        yield return new CellKernelStarted(runtime.RuntimeVersion, runtime.Epoch);

        if (!_executions.TryAdd(request.ExecutionId, linkedCancellation))
        {
            throw new DeckStateException(
                $"C# cell execution '{request.ExecutionId}' is already active.");
        }

        CSharpExecution? execution = null;
        var cancelled = false;
        string? errorId = null;
        string? error = null;
        try
        {
            execution = await session.ExecuteAsync(request, linkedCancellation.Token)
                .ConfigureAwait(false);
            cancelled = linkedCancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (CompilationErrorException exception)
        {
            errorId = "csharp.compilation";
            error = string.Join(Environment.NewLine, exception.Diagnostics);
        }
        catch (Exception exception)
        {
            errorId = "csharp.runtime";
            error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            _executions.TryRemove(request.ExecutionId, out _);
        }

        if (cancelled)
        {
            yield return new CellKernelCompleted(CellKernelExecutionStatus.Cancelled);
            yield break;
        }

        if (error is not null || execution is null)
        {
            yield return new CellKernelErrorProduced(
                errorId ?? "csharp.failed", error ?? "C# execution returned no result.");
            yield return new CellKernelCompleted(CellKernelExecutionStatus.Failed);
            yield break;
        }

        foreach (var line in execution.HostOutput.StandardOutput)
        {
            yield return new CellKernelTextProduced("stdout", line);
        }

        foreach (var line in execution.HostOutput.StandardError)
        {
            yield return new CellKernelTextProduced("stderr", line);
        }

        JsonElement value = default;
        string? translationError = null;
        try
        {
            value = CanonicalJson.ToElement(execution.ReturnValue);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            translationError = exception.Message;
        }

        if (translationError is not null)
        {
            yield return new CellKernelErrorProduced("csharp.non-portable-output", translationError);
            yield return new CellKernelCompleted(CellKernelExecutionStatus.Failed);
            yield break;
        }

        yield return new CellKernelValueProduced(value);
        yield return new CellKernelCompleted(CellKernelExecutionStatus.Succeeded);
    }

    public Task InterruptAsync(string executionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_executions.TryGetValue(executionId, out var execution))
        {
            execution.Cancel();
        }

        return Task.CompletedTask;
    }

    public async Task<CSharpRuntimeInfo> ReplaceAsync(
        string wraith,
        string? runId,
        string? haunt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var canonical = CanonicalName.Parse(wraith);
        var session = GetOrCreateSession(canonical.Value);
        var previousEpoch = session.Info.Epoch;
        var runtime = await session.ReplaceAsync(cancellationToken).ConfigureAwait(false);
        await _archive.AppendAsync(
            new ArchiveEvent(
                canonical.Value,
                "kernel.replaced",
                CanonicalJson.ToElement(new
                {
                    kernel = KernelId,
                    reason,
                    previousEpoch,
                    epoch = runtime.Epoch,
                    volatileStateLost = true,
                    replayedCells = false,
                }),
                haunt,
                runId,
                Timestamp: _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await _checkpoints.CheckpointAsync(
            "csharp-kernel-replaced",
            canonical,
            haunt is null ? null : CanonicalName.Parse(haunt),
            cancellationToken).ConfigureAwait(false);
        return runtime;
    }

    public CSharpRuntimeInfo? TryGetInfo(string wraith) =>
        _sessions.TryGetValue(CanonicalName.Parse(wraith).Value, out var session)
            ? session.Info
            : null;

    public void Dispose()
    {
        foreach (var execution in _executions.Values)
        {
            execution.Cancel();
        }

        _executions.Clear();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private CSharpSession GetOrCreateSession(string wraith)
    {
        var canonical = CanonicalName.Parse(wraith);
        return _sessions.GetOrAdd(
            canonical.Value,
            _ => new CSharpSession(canonical, _state, _artifacts, _clock));
    }

    private static ScriptOptions CreateOptions() => ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(JsonElement).Assembly,
            typeof(DurableValueScope).Assembly,
            typeof(DurableStateRuntime).Assembly,
            typeof(CSharpCellGlobals).Assembly)
        .WithImports(
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Text",
            "System.Text.Json",
            "System.Threading",
            "System.Threading.Tasks",
            "Deckwraith.Core.State");

    private sealed class CSharpSession : IDisposable
    {
        private readonly CanonicalName _wraith;
        private readonly DurableStateRuntime _stateRuntime;
        private readonly ArtifactRuntime _artifactRuntime;
        private readonly IDeckClock _clock;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ScriptState<object?>? _state;
        private CSharpCellGlobals? _globals;
        private bool _disposed;

        public CSharpSession(
            CanonicalName wraith,
            DurableStateRuntime stateRuntime,
            ArtifactRuntime artifactRuntime,
            IDeckClock clock)
        {
            _wraith = wraith;
            _stateRuntime = stateRuntime;
            _artifactRuntime = artifactRuntime;
            _clock = clock;
            Info = new CSharpRuntimeInfo(
                wraith.Value,
                1,
                clock.UtcNow,
                false,
                typeof(CSharpScript).Assembly.GetName().Version?.ToString() ?? "unknown");
        }

        public CSharpRuntimeInfo Info { get; private set; }

        public async Task<CSharpExecution> ExecuteAsync(
            CellExecutionRequest request,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_globals is null)
                {
                    var host = new CSharpKernelHost(
                        _stateRuntime, _artifactRuntime, request, cancellationToken);
                    _globals = new CSharpCellGlobals(host, request.Input.Clone());
                }
                else
                {
                    _globals.DwCellInput = request.Input.Clone();
                    _globals.Dw.BeginInvocation(request, cancellationToken);
                }

                _state = _state is null
                    ? await CSharpScript.RunAsync<object?>(
                        request.Source,
                        Options,
                        _globals,
                        typeof(CSharpCellGlobals),
                        cancellationToken).ConfigureAwait(false)
                    : await _state.ContinueWithAsync<object?>(
                        request.Source,
                        Options,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                return new CSharpExecution(_state.ReturnValue, _globals.Dw.EndInvocation());
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<CSharpRuntimeInfo> ReplaceAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _state = null;
                _globals = null;
                Info = new CSharpRuntimeInfo(
                    _wraith.Value,
                    checked(Info.Epoch + 1),
                    _clock.UtcNow,
                    true,
                    Info.RuntimeVersion);
                return Info;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _state = null;
            _globals = null;
            _gate.Dispose();
        }
    }

    private sealed record CSharpExecution(object? ReturnValue, CSharpHostOutput HostOutput);
}
