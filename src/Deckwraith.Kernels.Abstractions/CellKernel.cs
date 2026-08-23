using System.Text.Json;

namespace Deckwraith.Kernels.Abstractions;

public sealed record KernelCapabilities(
    bool Streaming,
    bool StructuredValues,
    bool Interruption,
    bool AmbientState);

public sealed record CellExecutionRequest(
    string ExecutionId,
    string Wraith,
    string? RunId,
    string Haunt,
    string CellName,
    string Source,
    JsonElement Input);

public enum CellKernelExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    OutcomeUnknown,
}

public abstract record CellKernelEvent;

public sealed record CellKernelStarted(
    string KernelVersion,
    long KernelEpoch) : CellKernelEvent;

public sealed record CellKernelValueProduced(JsonElement Value) : CellKernelEvent;

public sealed record CellKernelTextProduced(
    string Stream,
    string Text) : CellKernelEvent;

public sealed record CellKernelErrorProduced(
    string ErrorId,
    string Message) : CellKernelEvent;

public sealed record CellKernelCompleted(
    CellKernelExecutionStatus Status) : CellKernelEvent;

public interface ICellKernel
{
    string KernelId { get; }

    KernelCapabilities Capabilities { get; }

    IAsyncEnumerable<CellKernelEvent> ExecuteAsync(
        CellExecutionRequest request,
        CancellationToken cancellationToken);

    Task InterruptAsync(string executionId, CancellationToken cancellationToken);
}

public interface ICellKernelRegistry
{
    ICellKernel GetKernel(string kernelId);
}

public sealed class CellKernelRegistry : ICellKernelRegistry
{
    private readonly Dictionary<string, ICellKernel> _kernels;

    public CellKernelRegistry(IEnumerable<ICellKernel> kernels)
    {
        _kernels = kernels.ToDictionary(
            kernel => kernel.KernelId, StringComparer.OrdinalIgnoreCase);
        if (_kernels.Count == 0)
        {
            throw new ArgumentException("At least one cell kernel is required.", nameof(kernels));
        }
    }

    public ICellKernel GetKernel(string kernelId) =>
        _kernels.TryGetValue(kernelId, out var kernel)
            ? kernel
            : throw new KeyNotFoundException($"Cell kernel '{kernelId}' is not registered.");
}
