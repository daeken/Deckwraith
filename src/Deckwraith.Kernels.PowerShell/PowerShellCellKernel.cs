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
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
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
                BuildScript(request),
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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

        yield return new CellKernelStarted(
            typeof(PSObject).Assembly.GetName().Version?.ToString() ?? "unknown",
            result.ExecutionEpoch);
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

    private static string BuildScript(CellExecutionRequest request)
    {
        var input = request.Input.GetRawText();
        return $$"""
            $global:DwCellInput = ConvertFrom-Json -InputObject {{QuotePowerShell(input)}} -AsHashtable
            {{request.Source}}
            """;
    }

    private static string QuotePowerShell(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
