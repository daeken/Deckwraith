using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Core.Context;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.IntegrationTests;

public sealed class InferenceSpineEndToEndTests
{
    [Fact]
    public async Task FakeProviderTurnPersistsContextToolsElisionAndOperationLifecycles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using (var stateSpine = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            new FixedClock()))
        {
            await stateSpine.InitializeAsync(CancellationToken.None);
            await stateSpine.CreateHauntAsync("deckwraith", CancellationToken.None);
            await stateSpine.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var provider = new ScriptedProvider();
        var tools = new RecordingToolBroker();
        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([provider]),
            tools,
            new FixedClock(),
            defaultToolElisionTurns: 0);
        var started = await runtime.StartRunAsync(
            "wraith1",
            "deckwraith",
            "Prove the inference spine",
            "fake",
            "test-model",
            CancellationToken.None);

        var first = await runtime.ExecuteTurnAsync(
            "wraith1", started.Run.RunId, "Use the test tool.", CancellationToken.None);

        Assert.Equal("tool accepted", first.Text);
        Assert.Equal(1, first.Context.Turn);
        Assert.Equal(RunStatus.AwaitingInput, first.Run.Status);
        Assert.Contains(first.Context.Items, item => item.Kind is ContextItemKind.ToolInteraction);
        Assert.Equal(1, tools.ExecutionCount);
        Assert.Equal(2, provider.InvocationCount);

        var second = await runtime.ExecuteTurnAsync(
            "wraith1", started.Run.RunId, "Continue.", CancellationToken.None);

        Assert.Equal("second turn", second.Text);
        Assert.Equal(2, second.Context.Turn);
        var marker = Assert.Single(
            second.Context.Items, item => item.Kind is ContextItemKind.ToolElision);
        Assert.Null(marker.Input);
        Assert.Null(marker.Output);
        Assert.Equal(3, provider.InvocationCount);

        var records = await archive.ReadAllAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"), CancellationToken.None);
        var toolStarted = Assert.Single(records, record => record.Kind == "tool.started");
        var toolCompleted = Assert.Single(records, record => record.Kind == "tool.completed");
        Assert.Equal(toolStarted.EventId, marker.OperationId);
        Assert.True(toolCompleted.Payload.GetProperty("output").GetProperty("accepted").GetBoolean());
        Assert.Contains(records, record => record.Kind == "context.tools-elided");

        var persisted = await inferenceState.ReadContextAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(CanonicalJson.Hash(second.Context), CanonicalJson.Hash(persisted));
        Assert.Equal(string.Empty, await StateSpineEndToEndTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"], CancellationToken.None));
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(true, true, false, false, false);

        public int InvocationCount { get; private set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            Assert.Equal("wraith1", request.Identity.Name);
            Assert.True(request.Identity.Calibration.ContainsKey("register"));
            Assert.Equal(request.Manifest.IdentityHash, CanonicalJson.Hash(request.Identity));
            yield return new ModelResponseStarted($"provider-{InvocationCount}");
            if (InvocationCount == 1)
            {
                yield return new ModelToolCallCompleted(
                    "call-1",
                    "Test-DwInference",
                    JsonSerializer.SerializeToElement(new { value = 42 }));
                yield return new ModelUsageReported(100, 10, 50);
                yield return new ModelResponseCompleted(ModelFinishReason.ToolCalls, null);
            }
            else if (InvocationCount == 2)
            {
                Assert.Contains(request.Context.Items, item =>
                    item.Kind is ContextItemKind.ToolInteraction && item.OperationId is not null);
                yield return new ModelTextDelta("tool accepted");
                yield return new ModelUsageReported(120, 5, 70);
                yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
            }
            else
            {
                Assert.Contains(request.Context.Items, item => item.Kind is ContextItemKind.ToolElision);
                yield return new ModelTextDelta("second turn");
                yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RecordingToolBroker : IToolBroker
    {
        public IReadOnlyList<ModelToolDefinition> Tools { get; } =
        [
            new(
                "Test-DwInference",
                "Returns a structured acknowledgement.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { value = new { type = "integer" } },
                    required = new[] { "value" },
                })),
        ];

        public int ExecutionCount { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            string tool,
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            Assert.Equal("Test-DwInference", tool);
            Assert.Equal(42, arguments.GetProperty("value").GetInt32());
            Assert.Equal("wraith1", context.Agent);
            return Task.FromResult(new ToolExecutionResult(
                OperationStatus.Completed,
                JsonSerializer.SerializeToElement(new { accepted = true }),
                null));
        }
    }

    private sealed class FixedClock : Deckwraith.Application.Abstractions.IDeckClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 23, 20, 15, 0, TimeSpan.Zero);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"deckwraith-inference-{Guid.NewGuid():N}");
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
