using System.Management.Automation;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Mcp;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Tests;

public sealed class McpPowerShellProxyTests
{
    [Fact]
    public async Task AssignedMcpToolIsDiscoverableObjectNativeExplicitAndColdReloaded()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clock = new FixedClock();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState, archive, artifactStore, checkpoints, clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var marker = Path.Combine(temporaryDirectory.Path, "mcp-proxy-side-effect.txt");
        var serverAssembly = Path.Combine(
            AppContext.BaseDirectory, "Deckwraith.Mcp.TestServer.dll");
        using var mcp = new McpCatalogRuntime(
            temporaryDirectory.Path, deckState, archive, checkpoints, clock);
        await mcp.ConfigureServersAsync(
        [
            new McpServerDefinition(
                "fake",
                "dotnet",
                [serverAssembly, marker],
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                10),
        ]);
        await mcp.WriteWraithAssignmentAsync("wraith1", new McpAssignmentDocument(
            McpAssignmentDocument.CurrentSchemaVersion,
            ["fake"],
            [],
            [],
            ["fake/hidden_probe"],
            clock.UtcNow));

        var durable = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        var artifacts = new ArtifactRuntime(
            deckState, artifactStore, archive, checkpoints, clock);
        var manager = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            durable,
            artifacts,
            archive,
            checkpoints,
            clock,
            mcp);
        using var broker = new PowerShellToolBroker(manager);
        var modelTool = Assert.Single(broker.Tools);
        Assert.Equal(PowerShellToolBroker.ToolName, modelTool.Name);
        Assert.DoesNotContain("emit_structured_side_effect", modelTool.InputSchema.GetRawText());

        const string commandName = "Invoke-DwFakeEmitStructuredSideEffect";
        var invocation = new PowerShellInvocationContext(
            "wraith1", "run-1", null, "shell-1", "outer-tool-1");
        var discovery = await manager.ExecuteAsync(invocation, $$"""
            $volatileBeforeCatalogChange = 99
            $command = Get-Command {{commandName}}
            $help = Get-Help {{commandName}} -Full
            $schema = Get-DwToolSchema {{commandName}}
            $found = @(Find-DwCommand -Capability 'nested structured object')
            [pscustomobject]@{
                Name = $command.Name
                Module = $command.ModuleName
                LabelType = $command.Parameters['Label'].ParameterType.FullName
                CountType = $command.Parameters['Count'].ParameterType.FullName
                LabelMandatory = [bool]($command.Parameters['Label'].Attributes |
                    Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
                    Select-Object -First 1).Mandatory
                Synopsis = $help.Synopsis
                Schema = $schema.InputSchema.GetRawText()
                Found = $found[0].Command
            }
            """);

        Assert.Empty(discovery.Errors);
        var metadata = Assert.Single(discovery.Output);
        Assert.Equal(commandName, Property<string>(metadata, "Name"));
        Assert.Equal("Deckwraith.Mcp.Fake", Property<string>(metadata, "Module"));
        Assert.Equal("System.String", Property<string>(metadata, "LabelType"));
        Assert.Equal("System.Int64", Property<string>(metadata, "CountType"));
        Assert.True(Property<bool>(metadata, "LabelMandatory"));
        Assert.Contains("explicit marker", Property<string>(metadata, "Synopsis"));
        Assert.Contains("\"minimum\":1", Property<string>(metadata, "Schema"));
        Assert.Equal(commandName, Property<string>(metadata, "Found"));
        Assert.False(File.Exists(marker));

        var execution = await manager.ExecuteAsync(invocation, $$"""
            {{commandName}} -Label 'through-pipeline' -Count 4 |
                Select-Object label, count, @{ Name = 'Preserved'; Expression = { $_.nested.preserved } }
            """);
        Assert.Empty(execution.Errors);
        var structured = Assert.Single(execution.Output);
        Assert.Equal("through-pipeline", Property<string>(structured, "label"));
        Assert.Equal(4L, Property<long>(structured, "count"));
        Assert.True(Property<bool>(structured, "Preserved"));
        Assert.Equal(["through-pipeline:4"], await File.ReadAllLinesAsync(marker));

        await mcp.WriteWraithAssignmentAsync("wraith1", new McpAssignmentDocument(
            McpAssignmentDocument.CurrentSchemaVersion,
            [],
            [],
            ["fake"],
            [],
            clock.UtcNow));
        var removed = await manager.ExecuteAsync(invocation, $$"""
            [pscustomobject]@{
                Exists = [bool](Get-Command {{commandName}} -ErrorAction SilentlyContinue)
                VolatileExists = [bool](Test-Path variable:volatileBeforeCatalogChange)
            }
            """);
        Assert.True(removed.ToolsReloaded);
        Assert.Equal(2, removed.Runtime.Epoch);
        var removal = Assert.Single(removed.Output);
        Assert.False(Property<bool>(removal, "Exists"));
        Assert.False(Property<bool>(removal, "VolatileExists"));
        Assert.Equal(["through-pipeline:4"], await File.ReadAllLinesAsync(marker));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Single(records, record => record.Kind == "mcp.started");
        Assert.Single(records, record => record.Kind == "mcp.completed");
        var replacement = Assert.Single(records, record =>
            record.Kind == "runspace.replaced" &&
            record.Payload.GetProperty("reason").GetString() == "mcp-catalog-changed");
        Assert.False(replacement.Payload.GetProperty("replayedCommands").GetBoolean());
        Assert.Equal(string.Empty, await RunspaceLossTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private static T Property<T>(PSObject value, string name)
    {
        object? result = value.Properties[name].Value;
        while (result is PSObject wrapper &&
               wrapper.BaseObject is { } baseObject &&
               !ReferenceEquals(wrapper, baseObject))
        {
            result = baseObject;
        }

        return Assert.IsType<T>(result);
    }

    private sealed class FixedClock : IDeckClock
    {
        private long _ticks;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddTicks(
            Interlocked.Increment(ref _ticks));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"deckwraith-mcp-powershell-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
