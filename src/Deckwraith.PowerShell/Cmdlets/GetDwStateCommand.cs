using System.Management.Automation;
using Deckwraith.Core.State;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwState")]
[OutputType(typeof(object), typeof(DurableValueRecord))]
public sealed class GetDwStateCommand : DwCmdlet
{
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    [Parameter]
    public DurableValueScope Scope { get; set; } = DurableValueScope.Agent;

    [Parameter]
    public SwitchParameter Record { get; set; }

    protected override void ProcessRecord()
    {
        var invocation = RuntimeSession.Invocation;
        if (string.IsNullOrWhiteSpace(Name))
        {
            var records = RuntimeSession.DurableState.ListAsync(
                invocation.Wraith,
                Scope,
                invocation.RunId,
                invocation.Haunt,
                CancellationToken.None).GetAwaiter().GetResult();
            WriteObject(records, enumerateCollection: true);
            return;
        }

        var value = RuntimeSession.DurableState.GetAsync(
            invocation.Wraith,
            Scope,
            Name,
            invocation.RunId,
            invocation.Haunt,
            CancellationToken.None).GetAwaiter().GetResult();
        if (value is null)
        {
            return;
        }

        WriteObject(Record.IsPresent
            ? value
            : PortablePowerShellValue.FromJsonElement(value.Value));
    }
}
