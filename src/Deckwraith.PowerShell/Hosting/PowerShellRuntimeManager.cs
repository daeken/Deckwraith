using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Mcp;
using Deckwraith.PowerShell.Cmdlets;

namespace Deckwraith.PowerShell.Hosting;

public sealed class PowerShellRuntimeManager : IDisposable
{
    private static readonly string[] DefaultModules =
    [
        "Microsoft.PowerShell.Management",
        "Microsoft.PowerShell.Utility",
    ];

    private readonly string _rootPath;
    private readonly DurableStateRuntime _durableState;
    private readonly ArtifactRuntime _artifacts;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;
    private readonly McpCatalogRuntime? _mcp;
    private readonly bool _ownsMcp;
    private readonly ConcurrentDictionary<string, WraithPowerShellSession> _sessions =
        new(StringComparer.Ordinal);

    public PowerShellRuntimeManager(
        string rootPath,
        DurableStateRuntime durableState,
        ArtifactRuntime artifacts,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null,
        McpCatalogRuntime? mcp = null,
        bool ownsMcp = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _durableState = durableState;
        _artifacts = artifacts;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
        _mcp = mcp;
        _ownsMcp = ownsMcp;
    }

    public async Task<PowerShellExecutionResult> ExecuteAsync(
        PowerShellInvocationContext invocation,
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation.Wraith);
        ArgumentNullException.ThrowIfNull(script);
        var wraith = CanonicalName.Parse(invocation.Wraith);
        var normalized = invocation with { Wraith = wraith.Value };
        var catalog = _mcp is null
            ? null
            : await _mcp.GetEffectiveCatalogAsync(
                wraith.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = GetOrCreateSession(wraith, catalog);
        return await session.ExecuteAsync(normalized, script, catalog, cancellationToken)
            .ConfigureAwait(false);
    }

    public PowerShellRuntimeInfo EnsureRuntime(string wraith) =>
        GetOrCreateSession(CanonicalName.Parse(wraith), null).Info;

    public async Task<PowerShellRuntimeInfo> ReplaceAsync(
        PowerShellInvocationContext invocation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var wraith = CanonicalName.Parse(invocation.Wraith);
        var normalized = invocation with { Wraith = wraith.Value };
        var catalog = _mcp is null
            ? null
            : await _mcp.GetEffectiveCatalogAsync(
                wraith.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = GetOrCreateSession(wraith, catalog);
        return await session.ReplaceAsync(normalized, reason, catalog, cancellationToken)
            .ConfigureAwait(false);
    }

    public PowerShellRuntimeInfo? TryGetInfo(string wraith) =>
        _sessions.TryGetValue(CanonicalName.Parse(wraith).Value, out var session)
            ? session.Info
            : null;

    private WraithPowerShellSession GetOrCreateSession(
        CanonicalName wraith,
        McpEffectiveCatalog? catalog)
    {
        if (_sessions.TryGetValue(wraith.Value, out var existing))
        {
            return existing;
        }

        var created = new WraithPowerShellSession(
            _rootPath,
            wraith,
            _durableState,
            _artifacts,
            _archive,
            _checkpoints,
            _clock,
            _mcp,
            catalog);
        var selected = _sessions.GetOrAdd(wraith.Value, created);
        if (!ReferenceEquals(selected, created))
        {
            created.Dispose();
        }

        return selected;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        if (_ownsMcp)
        {
            _mcp?.Dispose();
        }
    }

    private sealed class WraithPowerShellSession : IDisposable
    {
        private readonly string _rootPath;
        private readonly CanonicalName _wraith;
        private readonly IAgentArchive _archive;
        private readonly ICheckpointStore _checkpoints;
        private readonly IDeckClock _clock;
        private readonly McpCatalogRuntime? _mcp;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly PowerShellSessionContext _sessionContext;
        private Runspace _runspace;
        private PowerShellRuntimeInfo _info;
        private McpEffectiveCatalog? _catalog;
        private bool _disposed;

        public WraithPowerShellSession(
            string rootPath,
            CanonicalName wraith,
            DurableStateRuntime durableState,
            ArtifactRuntime artifacts,
            IAgentArchive archive,
            ICheckpointStore checkpoints,
            IDeckClock clock,
            McpCatalogRuntime? mcp,
            McpEffectiveCatalog? catalog)
        {
            _rootPath = rootPath;
            _wraith = wraith;
            _archive = archive;
            _checkpoints = checkpoints;
            _clock = clock;
            _mcp = mcp;
            _catalog = catalog;
            _info = new PowerShellRuntimeInfo(
                wraith.Value,
                1,
                clock.UtcNow,
                false,
                [],
                catalog?.ContentHash,
                DescribeMcpTools(catalog));
            _sessionContext = new PowerShellSessionContext(
                durableState, artifacts, mcp, catalog, () => _info);
            var candidate = BuildCandidate(catalog);
            _runspace = candidate.Runspace;
            _info = _info with { Tools = candidate.Tools };
        }

        public PowerShellRuntimeInfo Info => _info;

        public async Task<PowerShellExecutionResult> ExecuteAsync(
            PowerShellInvocationContext invocation,
            string script,
            McpEffectiveCatalog? catalog,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessionContext.SetInvocation(invocation);
                var toolsReloaded = false;
                if (!StringComparer.Ordinal.Equals(
                    _catalog?.ContentHash, catalog?.ContentHash))
                {
                    await ReplaceUnderLockAsync(
                        invocation,
                        "mcp-catalog-changed",
                        catalog,
                        cancellationToken).ConfigureAwait(false);
                    toolsReloaded = true;
                }

                var executionEpoch = _info.Epoch;
                IReadOnlyList<PSObject> output;
                IReadOnlyList<ErrorRecord> errors;
                using (var powershell = System.Management.Automation.PowerShell.Create())
                {
                    powershell.Runspace = _runspace;
                    powershell.AddScript(script, useLocalScope: false);
                    using var registration = cancellationToken.Register(powershell.Stop);
                    var invoked = await Task.Run(powershell.Invoke, cancellationToken)
                        .ConfigureAwait(false);
                    output = invoked.ToArray();
                    errors = powershell.Streams.Error.ToArray();
                }

                if (_sessionContext.ConsumeToolReloadRequest())
                {
                    var refreshedCatalog = _mcp is null
                        ? catalog
                        : await _mcp.GetEffectiveCatalogAsync(
                            _wraith.Value,
                            forceRefresh: true,
                            cancellationToken).ConfigureAwait(false);
                    await ReplaceUnderLockAsync(
                        invocation,
                        "tool-catalog-reloaded",
                        refreshedCatalog,
                        cancellationToken).ConfigureAwait(false);
                    toolsReloaded = true;
                }

                return new PowerShellExecutionResult(
                    output, errors, _info, executionEpoch, toolsReloaded);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PowerShellRuntimeInfo> ReplaceAsync(
            PowerShellInvocationContext invocation,
            string reason,
            McpEffectiveCatalog? catalog,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessionContext.SetInvocation(invocation);
                await ReplaceUnderLockAsync(invocation, reason, catalog, cancellationToken)
                    .ConfigureAwait(false);
                return _info;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runspace.Dispose();
            _gate.Dispose();
        }

        private async Task ReplaceUnderLockAsync(
            PowerShellInvocationContext invocation,
            string reason,
            McpEffectiveCatalog? catalog,
            CancellationToken cancellationToken)
        {
            CandidateRunspace candidate;
            try
            {
                candidate = BuildCandidate(catalog);
            }
            catch (PowerShellToolLoadException exception)
            {
                await AppendLifecycleEventAsync(
                    invocation,
                    "runspace.reload-failed",
                    new { reason, error = exception.Message },
                    "powershell-tool-reload-failed",
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            var previous = _runspace;
            var previousEpoch = _info.Epoch;
            _runspace = candidate.Runspace;
            _catalog = catalog;
            _sessionContext.SetMcpCatalog(catalog);
            _info = new PowerShellRuntimeInfo(
                _wraith.Value,
                checked(previousEpoch + 1),
                _clock.UtcNow,
                true,
                candidate.Tools,
                catalog?.ContentHash,
                DescribeMcpTools(catalog));
            previous.Dispose();
            await AppendLifecycleEventAsync(
                invocation,
                "runspace.replaced",
                new
                {
                    reason,
                    previousEpoch,
                    epoch = _info.Epoch,
                    replayedCommands = false,
                    tools = candidate.Tools.Select(tool => new
                    {
                        source = Path.GetFileName(tool.SourcePath),
                        tool.ContentHash,
                    }).ToArray(),
                    mcpCatalogHash = catalog?.ContentHash,
                    mcpTools = catalog?.Tools.Select(tool => new
                    {
                        tool.QualifiedName,
                        tool.PowerShellModule,
                        tool.PowerShellCommand,
                    }).ToArray(),
                },
                reason is "tool-catalog-reloaded" or "mcp-catalog-changed"
                    ? "powershell-tools-reloaded"
                    : "powershell-runspace-replaced",
                cancellationToken).ConfigureAwait(false);
        }

        private async Task AppendLifecycleEventAsync(
            PowerShellInvocationContext invocation,
            string kind,
            object payload,
            string checkpointReason,
            CancellationToken cancellationToken)
        {
            await _archive.AppendAsync(
                new ArchiveEvent(
                    _wraith.Value,
                    kind,
                    CanonicalJson.ToElement(payload),
                    invocation.Haunt,
                    invocation.RunId,
                    Timestamp: _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await _checkpoints.CheckpointAsync(
                checkpointReason,
                _wraith,
                invocation.Haunt is null ? null : CanonicalName.Parse(invocation.Haunt),
                cancellationToken).ConfigureAwait(false);
        }

        private CandidateRunspace BuildCandidate(McpEffectiveCatalog? catalog)
        {
            var initialState = InitialSessionState.CreateDefault2();
            initialState.LanguageMode = PSLanguageMode.FullLanguage;
            initialState.ImportPSModule(DefaultModules);
            AddCmdlet<GetDwStateCommand>(initialState, "Get-DwState");
            AddCmdlet<SetDwStateCommand>(initialState, "Set-DwState");
            AddCmdlet<RemoveDwStateCommand>(initialState, "Remove-DwState");
            AddCmdlet<GetDwArtifactCommand>(initialState, "Get-DwArtifact");
            AddCmdlet<SetDwArtifactCommand>(initialState, "Set-DwArtifact");
            AddCmdlet<GetDwRuntimeCommand>(initialState, "Get-DwRuntime");
            AddCmdlet<GetDwToolCommand>(initialState, "Get-DwTool");
            AddCmdlet<GetDwToolSchemaCommand>(initialState, "Get-DwToolSchema");
            AddCmdlet<FindDwCommandCommand>(initialState, "Find-DwCommand");
            AddCmdlet<InvokeDwMcpToolCommand>(initialState, "Invoke-DwMcpTool");
            AddCmdlet<ReloadDwToolsCommand>(initialState, "Update-DwTools");
            initialState.Commands.Add(new SessionStateAliasEntry(
                "Reload-DwTools", "Update-DwTools"));
            var runspace = RunspaceFactory.CreateRunspace(initialState);
            try
            {
                runspace.Open();
                runspace.SessionStateProxy.SetVariable(
                    DwCmdlet.SessionVariableName, _sessionContext);
                LoadMcpTools(runspace, catalog);
                var tools = LoadTools(runspace);
                return new CandidateRunspace(runspace, tools);
            }
            catch
            {
                runspace.Dispose();
                throw;
            }
        }

        private static void LoadMcpTools(Runspace runspace, McpEffectiveCatalog? catalog)
        {
            if (catalog is null || catalog.Tools.Count == 0)
            {
                return;
            }

            foreach (var module in catalog.Tools.GroupBy(
                tool => tool.PowerShellModule, StringComparer.Ordinal))
            {
                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.Runspace = runspace;
                powershell.AddScript(McpPowerShellProxyBuilder.BuildModuleImport(
                    module.Key, module.ToArray()));
                _ = powershell.Invoke();
                if (powershell.HadErrors)
                {
                    var diagnostic = string.Join(
                        Environment.NewLine,
                        powershell.Streams.Error.Select(error => error.ToString()));
                    throw new PowerShellToolLoadException(
                        $"Could not generate MCP module '{module.Key}': {diagnostic}");
                }
            }
        }

        private List<PowerShellToolAssignment> LoadTools(Runspace runspace)
        {
            var toolsPath = Path.Combine(_rootPath, "agents", _wraith.Value, "tools");
            if (!Directory.Exists(toolsPath))
            {
                return [];
            }

            var assignments = new List<PowerShellToolAssignment>();
            foreach (var path in Directory.EnumerateFiles(toolsPath, "*.ps1").Order(StringComparer.Ordinal))
            {
                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.Runspace = runspace;
                powershell.AddScript($". {QuotePowerShell(path)}", useLocalScope: false);
                _ = powershell.Invoke();
                if (powershell.HadErrors)
                {
                    var diagnostic = string.Join(
                        Environment.NewLine,
                        powershell.Streams.Error.Select(error => error.ToString()));
                    throw new PowerShellToolLoadException(
                        $"Could not load authored tool '{Path.GetFileName(path)}': {diagnostic}");
                }

                var hash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
                assignments.Add(new PowerShellToolAssignment(path, hash));
            }

            return assignments;
        }

        private static void AddCmdlet<T>(InitialSessionState state, string name)
            where T : Cmdlet =>
            state.Commands.Add(new SessionStateCmdletEntry(name, typeof(T), helpFileName: null));

        private static string QuotePowerShell(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private static PowerShellMcpToolAssignment[] DescribeMcpTools(
            McpEffectiveCatalog? catalog) =>
            catalog?.Tools.Select(tool => new PowerShellMcpToolAssignment(
                tool.QualifiedName,
                tool.PowerShellModule,
                tool.PowerShellCommand,
                tool.Description)).ToArray() ?? [];

        private sealed record CandidateRunspace(
            Runspace Runspace,
            IReadOnlyList<PowerShellToolAssignment> Tools);
    }
}
