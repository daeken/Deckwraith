using System.Collections.Concurrent;
using System.Management.Automation;
using System.Runtime.CompilerServices;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.Kernels.PowerShell;

public sealed class PowerShellCellKernel : ICellKernel, IDisposable
{
    private readonly PowerShellRuntimeManager _runspaces;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _executions =
        new(StringComparer.Ordinal);

    public PowerShellCellKernel(PowerShellRuntimeManager runspaces)
    {
        _runspaces = runspaces;
    }

    public string KernelId => "powershell";

    public KernelCapabilities Capabilities { get; } = new(
        Streaming: false,
        StructuredValues: true,
        Interruption: true,
        AmbientState: true);

    public async IAsyncEnumerable<CellKernelEvent> ExecuteAsync(
        CellExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runtime = _runspaces.EnsureRuntime(request.Wraith);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        yield return new CellKernelStarted(
            typeof(PSObject).Assembly.GetName().Version?.ToString() ?? "unknown",
            runtime.Epoch);

        if (!_executions.TryAdd(request.ExecutionId, linkedCancellation))
        {
            throw new DeckStateException(
                $"PowerShell cell execution '{request.ExecutionId}' is already active.");
        }

        PowerShellExecutionResult? result = null;
        var cancelled = false;
        string? failure = null;
        try
        {
            result = await _runspaces.ExecuteAsync(
                new PowerShellInvocationContext(request.Wraith, request.RunId, request.Haunt),
                request.Source,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["DwCellInput"] = PortablePowerShellValue.FromJsonElement(request.Input),
                },
                linkedCancellation.Token).ConfigureAwait(false);
            cancelled = linkedCancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception) when (linkedCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
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

        if (failure is not null || result is null)
        {
            yield return new CellKernelErrorProduced(
                "powershell.host-failed", failure ?? "PowerShell execution returned no result.");
            yield return new CellKernelCompleted(CellKernelExecutionStatus.Failed);
            yield break;
        }

        var translationFailed = false;
        foreach (var value in result.Output)
        {
            CellKernelEvent translated;
            try
            {
                translated = new CellKernelValueProduced(
                    PortablePowerShellValue.ToJsonElement(value));
            }
            catch (DeckStateException exception)
            {
                translationFailed = true;
                translated = new CellKernelErrorProduced(
                    "powershell.non-portable-output", exception.Message);
            }

            yield return translated;
        }

        foreach (var error in result.Errors)
        {
            yield return new CellKernelErrorProduced(
                string.IsNullOrWhiteSpace(error.FullyQualifiedErrorId)
                    ? "powershell.error"
                    : error.FullyQualifiedErrorId,
                error.ToString());
        }

        yield return new CellKernelCompleted(
            result.Errors.Count == 0 && !translationFailed
                ? CellKernelExecutionStatus.Succeeded
                : CellKernelExecutionStatus.Failed);
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

    public void Dispose()
    {
        foreach (var execution in _executions.Values)
        {
            execution.Cancel();
        }

        _executions.Clear();
    }
}
