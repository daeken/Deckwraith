using System.Management.Automation;
using Deckwraith.Core.State;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Set, "DwState")]
[OutputType(typeof(DurableValueRecord))]
public sealed class SetDwStateCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true)]
    [AllowNull]
    public object? Value { get; set; }

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
        var result = RuntimeSession.DurableState.SetAsync(
            invocation.Wraith,
            Scope,
            Name,
            PortablePowerShellValue.ToJsonElement(Value),
            invocation.RunId,
            invocation.Haunt,
            expectedVersion,
            CancellationToken.None).GetAwaiter().GetResult();
        WriteObject(result.Value);
    }
}
