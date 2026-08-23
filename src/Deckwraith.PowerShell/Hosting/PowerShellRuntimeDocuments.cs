using System.Management.Automation;

namespace Deckwraith.PowerShell.Hosting;

public sealed record PowerShellInvocationContext(
    string Wraith,
    string? RunId = null,
    string? Haunt = null);

public sealed record PowerShellToolAssignment(
    string SourcePath,
    string ContentHash);

public sealed record PowerShellRuntimeInfo(
    string Wraith,
    long Epoch,
    DateTimeOffset StartedAt,
    bool VolatileStateLost,
    IReadOnlyList<PowerShellToolAssignment> Tools);

public sealed record PowerShellExecutionResult(
    IReadOnlyList<PSObject> Output,
    IReadOnlyList<ErrorRecord> Errors,
    PowerShellRuntimeInfo Runtime,
    bool ToolsReloaded);

public sealed record PowerShellToolReloadRequest(string Wraith);

public sealed class PowerShellToolLoadException : Exception
{
    public PowerShellToolLoadException(string message)
        : base(message)
    {
    }
}
