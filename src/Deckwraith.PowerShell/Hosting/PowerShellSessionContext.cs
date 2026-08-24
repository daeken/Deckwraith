using Deckwraith.Application.State;
using Deckwraith.Mcp;

namespace Deckwraith.PowerShell.Hosting;

internal sealed class PowerShellSessionContext
{
    private int _reloadRequested;

    public PowerShellSessionContext(
        DurableStateRuntime durableState,
        ArtifactRuntime artifactRuntime,
        McpCatalogRuntime? mcp,
        McpEffectiveCatalog? mcpCatalog,
        Func<PowerShellRuntimeInfo> getRuntimeInfo)
    {
        DurableState = durableState;
        Artifacts = artifactRuntime;
        Mcp = mcp;
        McpCatalog = mcpCatalog;
        GetRuntimeInfo = getRuntimeInfo;
        Invocation = new PowerShellInvocationContext(string.Empty);
    }

    public DurableStateRuntime DurableState { get; }

    public ArtifactRuntime Artifacts { get; }

    public McpCatalogRuntime? Mcp { get; }

    public McpEffectiveCatalog? McpCatalog { get; private set; }

    public Func<PowerShellRuntimeInfo> GetRuntimeInfo { get; }

    public PowerShellInvocationContext Invocation { get; private set; }

    public void SetInvocation(PowerShellInvocationContext invocation) => Invocation = invocation;

    public void SetMcpCatalog(McpEffectiveCatalog? catalog) => McpCatalog = catalog;

    public void RequestToolReload() => Interlocked.Exchange(ref _reloadRequested, 1);

    public bool ConsumeToolReloadRequest() => Interlocked.Exchange(ref _reloadRequested, 0) == 1;
}
