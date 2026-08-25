using System.Management.Automation;
using Deckwraith.Core.Serialization;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwRuntime")]
[OutputType(typeof(PSObject))]
public sealed class GetDwRuntimeCommand : DwCmdlet
{
    protected override void ProcessRecord() => WriteObject(
        PortablePowerShellValue.FromJsonElement(
            CanonicalJson.ToElement(RuntimeSession.GetRuntimeInfo())),
        enumerateCollection: false);
}
