using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Find, "DwCommand")]
[OutputType(typeof(PowerShellMcpToolAssignment))]
public sealed class FindDwCommandCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Query")]
    [ValidateNotNullOrEmpty]
    public string Capability { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var matches = RuntimeSession.McpCatalog?.Tools.Where(tool =>
            tool.QualifiedName.Contains(Capability, StringComparison.OrdinalIgnoreCase) ||
            tool.PowerShellCommand.Contains(Capability, StringComparison.OrdinalIgnoreCase) ||
            tool.Description.Contains(Capability, StringComparison.OrdinalIgnoreCase) ||
            tool.InputSchema.GetRawText().Contains(Capability, StringComparison.OrdinalIgnoreCase))
            .Select(tool => new PowerShellMcpToolAssignment(
                tool.QualifiedName,
                tool.PowerShellModule,
                tool.PowerShellCommand,
                tool.Description))
            .ToArray() ?? [];
        WriteObject(matches, enumerateCollection: true);
    }
}
