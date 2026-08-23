using System.Management.Automation;
using Deckwraith.Core.State;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Remove, "DwState")]
[OutputType(typeof(DurableValueRecord))]
public sealed class RemoveDwStateCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public DurableValueScope Scope { get; set; } = DurableValueScope.Agent;

    [Parameter]
    public long ExpectedVersion { get; set; }

    protected override void ProcessRecord()
    {
        var invocation = RuntimeSession.Invocation;
        var expectedVersion = MyInvocation.BoundParameters.ContainsKey(nameof(ExpectedVersion))
            ? ExpectedVersion
            : (long?)null;
        var result = RuntimeSession.DurableState.RemoveAsync(
            invocation.Wraith,
            Scope,
            Name,
            invocation.RunId,
            invocation.Haunt,
            expectedVersion,
            CancellationToken.None).GetAwaiter().GetResult();
        if (result.Value is not null)
        {
            WriteObject(result.Value);
        }
    }
}
