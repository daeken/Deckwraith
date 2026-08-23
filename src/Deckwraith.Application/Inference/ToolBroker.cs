using System.Text.Json;
using Deckwraith.Core.Context;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Application.Inference;

public sealed record ToolExecutionContext(
    string Agent,
    string? Haunt,
    string RunId,
    string ShellId,
    string OperationId);

public sealed record ToolExecutionResult(
    OperationStatus Status,
    JsonElement Output,
    string? Error);

public interface IToolBroker
{
    IReadOnlyList<ModelToolDefinition> Tools { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        string tool,
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class EmptyToolBroker : IToolBroker
{
    public static EmptyToolBroker Instance { get; } = new();

    private EmptyToolBroker()
    {
    }

    public IReadOnlyList<ModelToolDefinition> Tools => [];

    public Task<ToolExecutionResult> ExecuteAsync(
        string tool,
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"No tool named '{tool}' is available.");
}
