using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwTool")]
[OutputType(typeof(PowerShellToolAssignment), typeof(PowerShellMcpToolAssignment))]
public sealed class GetDwToolCommand : DwCmdlet
{
    protected override void ProcessRecord()
    {
        var info = RuntimeSession.GetRuntimeInfo();
        WriteObject(info.Tools, enumerateCollection: true);
        WriteObject(info.McpTools ?? [], enumerateCollection: true);
    }
}
