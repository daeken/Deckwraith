using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwTool")]
[OutputType(typeof(PowerShellToolAssignment))]
public sealed class GetDwToolCommand : DwCmdlet
{
    protected override void ProcessRecord() =>
        WriteObject(RuntimeSession.GetRuntimeInfo().Tools, enumerateCollection: true);
}
