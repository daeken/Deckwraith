using System.Text.Json;
using Deckwraith.Application.Inference;
using Deckwraith.Core.Context;
using Deckwraith.Core.Serialization;
using Deckwraith.PowerShell.Serialization;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.PowerShell.Hosting;

public sealed class PowerShellToolBroker : IToolBroker, IDisposable
{
    public const string ToolName = "Invoke-PowerShell";

    private static readonly IReadOnlyList<ModelToolDefinition> ToolDefinitions =
    [
        new(
            ToolName,
            "Execute PowerShell in the wraith's persistent object-native runspace. " +
            "Use Get-Command, Get-Help, and Find-DwCommand to discover assigned commands.",
            CanonicalJson.ToElement(new
            {
                type = "object",
                properties = new
                {
                    script = new
                    {
                        type = "string",
                        description = "PowerShell source to execute.",
                    },
                },
                required = new[] { "script" },
                additionalProperties = false,
            }))
    ];

    private readonly PowerShellRuntimeManager _runtime;
    private bool _disposed;

    public PowerShellToolBroker(PowerShellRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public IReadOnlyList<ModelToolDefinition> Tools => ToolDefinitions;

    public async Task<ToolExecutionResult> ExecuteAsync(
        string tool,
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(tool, ToolName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No tool named '{tool}' is available.");
        }

        if (arguments.ValueKind is not JsonValueKind.Object ||
            !arguments.TryGetProperty("script", out var scriptElement) ||
            scriptElement.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(scriptElement.GetString()))
        {
            throw new ArgumentException(
                $"Tool '{ToolName}' requires a non-empty string property named 'script'.",
                nameof(arguments));
        }

        var unexpected = arguments.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !string.Equals(name, "script", StringComparison.Ordinal));
        if (unexpected is not null)
        {
            throw new ArgumentException(
                $"Tool '{ToolName}' does not accept property '{unexpected}'.",
                nameof(arguments));
        }

        var execution = await _runtime.ExecuteAsync(
            new PowerShellInvocationContext(context.Agent, context.RunId, context.Haunt),
            scriptElement.GetString()!,
            cancellationToken).ConfigureAwait(false);
        var output = CanonicalJson.ToElement(new
        {
            output = execution.Output.Select(PortablePowerShellValue.ToJsonElement).ToArray(),
            errors = execution.Errors.Select(error => new
            {
                error.FullyQualifiedErrorId,
                message = error.ToString(),
                category = error.CategoryInfo.Category.ToString(),
            }).ToArray(),
            runtime = new
            {
                execution.Runtime.Wraith,
                execution.Runtime.Epoch,
                execution.Runtime.StartedAt,
                execution.Runtime.VolatileStateLost,
                execution.ExecutionEpoch,
                execution.ToolsReloaded,
            },
        });
        return execution.Errors.Count == 0
            ? new ToolExecutionResult(OperationStatus.Completed, output, null)
            : new ToolExecutionResult(
                OperationStatus.Failed,
                output,
                string.Join(Environment.NewLine, execution.Errors.Select(error => error.ToString())));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Dispose();
    }
}
