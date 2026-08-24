using System.Management.Automation;
using System.Text;
using Deckwraith.Core.State;

namespace Deckwraith.PowerShell.Cmdlets;

[Cmdlet(VerbsCommon.Get, "DwArtifact")]
[OutputType(typeof(byte[]), typeof(string))]
public sealed class GetDwArtifactCommand : DwCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Hash { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter AsText { get; set; }

    protected override void ProcessRecord()
    {
        var invocation = RuntimeSession.Invocation;
        if (string.IsNullOrWhiteSpace(invocation.Haunt))
        {
            throw new DeckStateException("Artifact reads require a haunt execution context.");
        }

        var content = RuntimeSession.Artifacts.ReadAsync(
            invocation.Haunt,
            Hash,
            CancellationToken.None).GetAwaiter().GetResult();
        WriteObject(
            AsText.IsPresent ? Encoding.UTF8.GetString(content) : content,
            enumerateCollection: false);
    }
}
