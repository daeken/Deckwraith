using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Mcp;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.PowerShell.Tests;

public sealed class McpInferenceEndToEndTests
{
    [Fact]
    public async Task ModelDiscoversAndExplicitlyExecutesMcpThroughOnlyPowerShell()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clock = new FixedClock();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifacts = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState, archive, artifacts, checkpoints, clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var marker = Path.Combine(temporaryDirectory.Path, "model-mcp-side-effect.txt");
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

        var provider = new DiscoveringProvider(marker);
        var manager = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            new DurableStateRuntime(
                deckState,
                new JsonDurableValueStore(temporaryDirectory.Path),
                archive,
                checkpoints,
                clock),
            new ArtifactRuntime(deckState, artifacts, archive, checkpoints, clock),
            archive,
            checkpoints,
            clock,
            mcp);
        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([provider]),
            new PowerShellToolBroker(manager),
            clock);
        var started = await runtime.StartRunAsync(
            "wraith1", null, "Discover and execute the assigned capability", "fake", "test-model");
        var turn = await runtime.ExecuteTurnAsync(
            "wraith1", started.Run.RunId, "Find the structured side-effect command and use it.");

        Assert.Equal("MCP discovery and execution observed", turn.Text);
        Assert.Equal(2, provider.Invocations);
        Assert.Equal(["from-model:5"], await File.ReadAllLinesAsync(marker));
        var interaction = Assert.Single(turn.Context.Items, item =>
            item.Kind is ContextItemKind.ToolInteraction);
        Assert.Equal(PowerShellToolBroker.ToolName, interaction.Tool);
        Assert.Equal(OperationStatus.Completed, interaction.Status);
        Assert.Equal(
            "Invoke-DwFakeEmitStructuredSideEffect",
            interaction.Output!.Value.GetProperty("output")[0]
                .GetProperty("command").GetString());
        Assert.True(interaction.Output.Value.GetProperty("output")[0]
            .GetProperty("result").GetProperty("nested")
            .GetProperty("preserved").GetBoolean());

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Single(records, record => record.Kind == "tool.started");
        Assert.Single(records, record => record.Kind == "tool.completed");
        Assert.Single(records, record => record.Kind == "mcp.started");
        Assert.Single(records, record => record.Kind == "mcp.completed");
        Assert.Equal(string.Empty, await RunspaceLossTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private sealed class DiscoveringProvider : IModelProvider
    {
        private readonly string _marker;

        public DiscoveringProvider(string marker)
        {
            _marker = marker;
        }

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } =
            new(true, true, false, false, false);

        public int Invocations { get; private set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            var modelTool = Assert.Single(request.Tools);
            Assert.Equal(PowerShellToolBroker.ToolName, modelTool.Name);
            Assert.DoesNotContain("fake", modelTool.InputSchema.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                "emit_structured_side_effect",
                JsonSerializer.Serialize(request.Tools),
                StringComparison.Ordinal);
            yield return new ModelResponseStarted($"fake-{Invocations}");
            if (Invocations == 1)
            {
                Assert.False(File.Exists(_marker));
                yield return new ModelToolCallCompleted(
                    "discover-and-call",
                    PowerShellToolBroker.ToolName,
                    JsonSerializer.SerializeToElement(new
                    {
                        script = """
                            $command = Get-Command -Module Deckwraith.Mcp.* |
                                Where-Object Name -Like '*StructuredSideEffect'
                            $help = Get-Help $command.Name -Full
                            $result = & $command.Name -Label 'from-model' -Count 5
                            [pscustomobject]@{
                                command = $command.Name
                                synopsis = $help.Synopsis
                                result = $result
                            }
                            """,
                    }));
                yield return new ModelResponseCompleted(ModelFinishReason.ToolCalls, null);
            }
            else
            {
                var interaction = Assert.Single(request.Context.Items, item =>
                    item.Kind is ContextItemKind.ToolInteraction);
                Assert.Equal(OperationStatus.Completed, interaction.Status);
                var value = interaction.Output!.Value.GetProperty("output")[0];
                Assert.Contains("explicit marker", value.GetProperty("synopsis").GetString());
                Assert.Equal("from-model", value.GetProperty("result")
                    .GetProperty("label").GetString());
                Assert.True(value.GetProperty("result").GetProperty("nested")
                    .GetProperty("preserved").GetBoolean());
                yield return new ModelTextDelta("MCP discovery and execution observed");
                yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
            }

            await Task.CompletedTask;
        }
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
                System.IO.Path.GetTempPath(), $"deckwraith-mcp-inference-{Guid.NewGuid():N}");
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
