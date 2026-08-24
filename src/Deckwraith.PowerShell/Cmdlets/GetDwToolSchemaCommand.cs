using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwToolSchema")]
[OutputType(typeof(PowerShellMcpToolSchema))]
public sealed class GetDwToolSchemaCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNull]
    public object Command { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var commandName = Command switch
        {
            CommandInfo command => command.Name,
            _ => Command.ToString(),
        };
        var entry = RuntimeSession.McpCatalog?.Tools.SingleOrDefault(tool =>
            string.Equals(tool.PowerShellCommand, commandName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tool.QualifiedName, commandName, StringComparison.Ordinal));
        if (entry is null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"Assigned MCP command '{commandName}' was not found."),
                "Deckwraith.Mcp.CommandNotFound",
                ErrorCategory.ObjectNotFound,
                commandName));
            return;
        }

        WriteObject(new PowerShellMcpToolSchema(
            entry.QualifiedName,
            entry.PowerShellModule,
            entry.PowerShellCommand,
            entry.Description,
            entry.InputSchema.Clone(),
            entry.OutputSchema?.Clone()));
    }
}
