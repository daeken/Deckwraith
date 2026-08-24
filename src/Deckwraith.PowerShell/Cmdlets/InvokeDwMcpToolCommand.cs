using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using Deckwraith.Core.Serialization;
using Deckwraith.Mcp;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsLifecycle.Invoke, "DwMcpTool")]
[OutputType(typeof(object))]
public sealed class InvokeDwMcpToolCommand : DwCmdlet, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Server { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Tool { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNull]
    public Hashtable Arguments { get; set; } = new(StringComparer.Ordinal);

    protected override void ProcessRecord()
    {
        var runtime = RuntimeSession.Mcp ?? throw new PSInvalidOperationException(
            "This runspace has no MCP runtime.");
        var invocation = RuntimeSession.Invocation;
        var operationId = Guid.CreateVersion7().ToString("N");
        McpToolCallResult result;
        try
        {
            result = runtime.CallToolAsync(
                $"{Server}/{Tool}",
                PortablePowerShellValue.ToJsonElement(Arguments),
                new McpInvocationContext(
                    invocation.Wraith,
                    invocation.Haunt,
                    invocation.RunId,
                    invocation.ShellId,
                    operationId),
                _stopping.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            ThrowTerminatingError(new ErrorRecord(
                exception,
                "Deckwraith.Mcp.OutcomeUnknown",
                ErrorCategory.OperationStopped,
                $"{Server}/{Tool}"));
            return;
        }
        catch (Exception exception)
        {
            ThrowTerminatingError(new ErrorRecord(
                exception,
                "Deckwraith.Mcp.InvocationFailed",
                ErrorCategory.NotSpecified,
                $"{Server}/{Tool}"));
            return;
        }

        if (result.IsError)
        {
            ThrowTerminatingError(new ErrorRecord(
                new McpProtocolException(result.RawResult.GetRawText()),
                "Deckwraith.Mcp.ToolError",
                ErrorCategory.InvalidResult,
                $"{Server}/{Tool}"));
            return;
        }

        var value = result.StructuredContent.ValueKind is JsonValueKind.Object &&
            result.StructuredContent.EnumerateObject().Any()
            ? result.StructuredContent
            : result.Content;
        WriteObject(PortablePowerShellValue.FromJsonElement(value), enumerateCollection: false);
    }

    protected override void StopProcessing() => _stopping.Cancel();

    public void Dispose() => _stopping.Dispose();
}
