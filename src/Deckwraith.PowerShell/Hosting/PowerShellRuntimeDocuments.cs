using System.Management.Automation;
using System.Text.Json;

namespace Deckwraith.PowerShell.Hosting;

public sealed record PowerShellInvocationContext(
    string Wraith,
    string? RunId = null,
    string? Haunt = null,
    string? ShellId = null,
    string? OperationId = null);

public sealed record PowerShellToolAssignment(
    string SourcePath,
    string ContentHash);

public sealed record PowerShellRuntimeInfo(
    string Wraith,
    long Epoch,
    DateTimeOffset StartedAt,
    bool VolatileStateLost,
    IReadOnlyList<PowerShellToolAssignment> Tools,
    string? McpCatalogHash = null,
    IReadOnlyList<PowerShellMcpToolAssignment>? McpTools = null);

public sealed record PowerShellMcpToolAssignment(
    string QualifiedName,
    string Module,
    string Command,
    string Description);

public sealed record PowerShellMcpToolSchema(
    string QualifiedName,
    string Module,
    string Command,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record PowerShellExecutionResult(
    IReadOnlyList<PSObject> Output,
    IReadOnlyList<ErrorRecord> Errors,
    PowerShellRuntimeInfo Runtime,
    long ExecutionEpoch,
    bool ToolsReloaded);

public sealed record PowerShellToolReloadRequest(string Wraith);

public sealed class PowerShellToolLoadException : Exception
{
    public PowerShellToolLoadException(string message)
        : base(message)
    {
    }
}
