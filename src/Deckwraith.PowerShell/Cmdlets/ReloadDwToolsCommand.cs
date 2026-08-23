using System.Management.Automation;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsData.Update, "DwTools")]
[Alias("Reload-DwTools")]
[OutputType(typeof(PowerShellToolReloadRequest))]
public sealed class ReloadDwToolsCommand : DwCmdlet
{
    protected override void ProcessRecord()
    {
        RuntimeSession.RequestToolReload();
        WriteObject(new PowerShellToolReloadRequest(RuntimeSession.Invocation.Wraith));
    }
}
