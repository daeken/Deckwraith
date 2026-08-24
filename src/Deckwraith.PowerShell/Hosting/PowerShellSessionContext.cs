using Deckwraith.Application.State;

namespace Deckwraith.PowerShell.Hosting;

internal sealed class PowerShellSessionContext
{
    private int _reloadRequested;

    public PowerShellSessionContext(
        DurableStateRuntime durableState,
        ArtifactRuntime artifactRuntime,
        Func<PowerShellRuntimeInfo> getRuntimeInfo)
    {
        DurableState = durableState;
        Artifacts = artifactRuntime;
        GetRuntimeInfo = getRuntimeInfo;
        Invocation = new PowerShellInvocationContext(string.Empty);
    }

    public DurableStateRuntime DurableState { get; }

    public ArtifactRuntime Artifacts { get; }

    public Func<PowerShellRuntimeInfo> GetRuntimeInfo { get; }

    public PowerShellInvocationContext Invocation { get; private set; }

    public void SetInvocation(PowerShellInvocationContext invocation) => Invocation = invocation;

    public void RequestToolReload() => Interlocked.Exchange(ref _reloadRequested, 1);

    public bool ConsumeToolReloadRequest() => Interlocked.Exchange(ref _reloadRequested, 0) == 1;
}
