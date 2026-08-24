using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.PowerShell.Tests;

public sealed class PowerShellToolBrokerTests
{
    [Fact]
    public async Task ModelCanPersistStateThroughTheSinglePowerShellToolAndObserveItsResult()
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

        var durable = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        var artifactRuntime = new ArtifactRuntime(
            deckState, artifacts, archive, checkpoints, clock);
        var provider = new ToolLoopProvider();
        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([provider]),
            new PowerShellToolBroker(new PowerShellRuntimeManager(
                temporaryDirectory.Path,
                durable,
                artifactRuntime,
                archive,
                checkpoints,
                clock)),
            clock);
        var started = await runtime.StartRunAsync(
            "wraith1", null, "Bootstrap from inside", "fake", "test-model");
        var turn = await runtime.ExecuteTurnAsync(
            "wraith1", started.Run.RunId, "Persist the bootstrap marker.");

        Assert.Equal("bootstrap marker persisted", turn.Text);
        Assert.Equal(2, provider.Invocations);
        var interaction = Assert.Single(turn.Context.Items, item =>
            item.Kind is ContextItemKind.ToolInteraction);
        Assert.Equal(PowerShellToolBroker.ToolName, interaction.Tool);
        Assert.Equal(OperationStatus.Completed, interaction.Status);
        Assert.Equal(
            "bootstrap-marker",
            interaction.Output!.Value.GetProperty("output")[0]
                .GetProperty("Name").GetString());
        Assert.Equal(
            "inside",
            interaction.Output.Value.GetProperty("output")[1]
                .GetProperty("source").GetString());

        var persisted = await durable.GetAsync(
            "wraith1", DurableValueScope.Agent, "bootstrap-marker");
        Assert.NotNull(persisted);
        Assert.Equal("inside", persisted.Value.GetProperty("source").GetString());
        Assert.Equal(1, persisted.Version);

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        var toolStarted = Assert.Single(records, record => record.Kind == "tool.started");
        var toolCompleted = Assert.Single(records, record => record.Kind == "tool.completed");
        Assert.Equal(toolStarted.EventId, interaction.OperationId);
        Assert.Equal(
            toolStarted.EventId,
            toolCompleted.Payload.GetProperty("operationId").GetString());
        Assert.Equal(string.Empty, await RunspaceLossTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private sealed class ToolLoopProvider : IModelProvider
    {
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
            var tool = Assert.Single(request.Tools);
            Assert.Equal(PowerShellToolBroker.ToolName, tool.Name);
            Assert.Equal(
                "string",
                tool.InputSchema.GetProperty("properties")
                    .GetProperty("script").GetProperty("type").GetString());
            yield return new ModelResponseStarted($"fake-{Invocations}");
            if (Invocations == 1)
            {
                yield return new ModelToolCallCompleted(
                    "call-bootstrap",
                    PowerShellToolBroker.ToolName,
                    JsonSerializer.SerializeToElement(new
                    {
                        script = "[pscustomobject]@{ source = 'inside'; count = 1 } | " +
                            "Set-DwState -Name 'bootstrap-marker' -Scope Agent -ExpectedVersion 0; " +
                            "Get-DwState -Name 'bootstrap-marker'",
                    }));
                yield return new ModelResponseCompleted(ModelFinishReason.ToolCalls, null);
            }
            else
            {
                var interaction = Assert.Single(request.Context.Items, item =>
                    item.Kind is ContextItemKind.ToolInteraction);
                Assert.True(
                    interaction.Status is OperationStatus.Completed,
                    interaction.Output?.ToString());
                Assert.Equal(
                    "inside",
                    interaction.Output!.Value.GetProperty("output")[1]
                        .GetProperty("source").GetString());
                yield return new ModelTextDelta("bootstrap marker persisted");
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
                System.IO.Path.GetTempPath(), $"deckwraith-tool-broker-{Guid.NewGuid():N}");
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
