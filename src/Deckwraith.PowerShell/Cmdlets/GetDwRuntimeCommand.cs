using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwRuntime")]
[OutputType(typeof(PowerShellRuntimeInfo))]
public sealed class GetDwRuntimeCommand : DwCmdlet
{
    protected override void ProcessRecord() => WriteObject(RuntimeSession.GetRuntimeInfo());
}
