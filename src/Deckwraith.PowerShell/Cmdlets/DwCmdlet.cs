using System.Management.Automation;
using Deckwraith.Core.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Cmdlets;

public abstract class DwCmdlet : PSCmdlet
{
    internal const string SessionVariableName = "__DeckwraithSession";

    internal PowerShellSessionContext RuntimeSession =>
        SessionState.PSVariable.GetValue(SessionVariableName) as PowerShellSessionContext
        ?? throw new DeckStateException("This command requires a hosted Deckwraith runspace.");
}
