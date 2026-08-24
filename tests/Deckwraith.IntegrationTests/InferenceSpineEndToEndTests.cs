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
        RunStartResult started;
        TurnResult first;
        using (var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([provider]),
            tools,
            new FixedClock(),
            defaultToolElisionTurns: 0))
        {
            started = await runtime.StartRunAsync(
                "wraith1",
                "deckwraith",
                "Prove the inference spine",
                "fake",
                "test-model",
                CancellationToken.None);

            first = await runtime.ExecuteTurnAsync(
                "wraith1", started.Run.RunId, "Use the test tool.", CancellationToken.None);
        }

        Assert.Equal("tool accepted", first.Text);
        Assert.Equal(1, first.Context.Turn);
        Assert.Equal(RunStatus.AwaitingInput, first.Run.Status);
        Assert.Contains(first.Context.Items, item => item.Kind is ContextItemKind.ToolInteraction);
        Assert.Equal(1, tools.ExecutionCount);
        Assert.Equal(2, provider.InvocationCount);

        ShellReplacementResult replacement;
        TurnResult second;
        RunEndResult completedRun;
        using (var replacementRuntime = new InferenceRuntime(
            new JsonDeckStateStore(temporaryDirectory.Path),
            new JsonInferenceStateStore(temporaryDirectory.Path),
            new JsonlAgentArchive(temporaryDirectory.Path),
            new GitCheckpointStore(temporaryDirectory.Path),
            new ModelProviderRegistry([provider]),
            tools,
            new FixedClock(),
            defaultToolElisionTurns: 0))
        {
            replacement = await replacementRuntime.ReplaceShellAsync(
                "wraith1",
                started.Run.RunId,
                "fake",
                "replacement-model",
                "context-window-replaced",
                CancellationToken.None);
            second = await replacementRuntime.ExecuteTurnAsync(
                "wraith1", started.Run.RunId, "Continue.", CancellationToken.None);
            completedRun = await replacementRuntime.CompleteRunAsync(
                "wraith1",
                started.Run.RunId,
                "objective-achieved",
                CancellationToken.None);
        }

        Assert.Equal("second turn", second.Text);
        Assert.Equal(2, second.Context.Turn);
        Assert.Equal(2, replacement.Run.Shells.Count);
        Assert.Equal("context-window-replaced", replacement.PreviousShell.EndReason);
        Assert.NotNull(replacement.PreviousShell.EndedAt);
        Assert.Equal("replacement-model", replacement.CurrentShell.Model);
        Assert.Equal(RunStatus.Completed, completedRun.Run.Status);
        Assert.NotNull(completedRun.Shell.EndedAt);
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
        Assert.Equal(2, records.Count(record => record.Kind == "shell.started"));
        Assert.Equal(2, records.Count(record => record.Kind == "shell.ended"));
        var shellEnded = Assert.Single(records, record =>
            record.Kind == "shell.ended" &&
            record.ShellId == replacement.PreviousShell.ShellId);
        Assert.Equal(replacement.PreviousShell.ShellId, shellEnded.ShellId);
        Assert.Single(records, record => record.Kind == "run.completed");
        Assert.Contains(records, record =>
            record.Kind == "message.assistant" &&
            record.ShellId == replacement.CurrentShell.ShellId);

        var persisted = await inferenceState.ReadContextAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(CanonicalJson.Hash(second.Context), CanonicalJson.Hash(persisted));
        var rebuilt = ContextArchiveRebuilder.Rebuild(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"),
            records,
            second.Context.IdentityHash,
            second.Context.ToolElisionTurns,
            new FixedClock().UtcNow);
        Assert.Equal(CanonicalJson.Hash(second.Context), CanonicalJson.Hash(rebuilt));
        Assert.Equal(string.Empty, await StateSpineEndToEndTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"], CancellationToken.None));
    }

    [Fact]
    public async Task ProviderFailureClosesModelShellAndRunLifecycles()
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
            await stateSpine.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([new FailingProvider()]),
            clock: new FixedClock());
        var started = await runtime.StartRunAsync(
            "wraith1", null, "Exercise failure", "failing", "test-model");

        await Assert.ThrowsAsync<ModelInvocationException>(() => runtime.ExecuteTurnAsync(
            "wraith1", started.Run.RunId, "Fail now."));

        var records = await archive.ReadAllAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"), CancellationToken.None);
        var modelStarted = Assert.Single(records, record => record.Kind == "model.started");
        var modelFailed = Assert.Single(records, record => record.Kind == "model.failed");
        Assert.Equal(
            modelStarted.EventId,
            modelFailed.Payload.GetProperty("operationId").GetString());
        Assert.Single(records, record => record.Kind == "shell.ended");
        Assert.Single(records, record => record.Kind == "run.failed");
        var failedRun = await inferenceState.ReadRunAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith1"),
            started.Run.RunId,
            CancellationToken.None);
        Assert.Equal(RunStatus.Failed, failedRun.Status);
        Assert.NotNull(failedRun.Shells[^1].EndedAt);
        Assert.Equal(string.Empty, await StateSpineEndToEndTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"], CancellationToken.None));
    }

    [Fact]
    public async Task ModelContextAndArchiveStayPrivateToTheActiveWraith()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            new FixedClock()))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
            await state.CreateWraithAsync("wraith2", CancellationToken.None);
        }

        const string privateText = "private-wraith1-evidence-7cb1";
        var provider = new CapturingProvider();
        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([provider]),
            clock: new FixedClock());
        var first = await runtime.StartRunAsync(
            "wraith1", null, "Hold private context", "capture", "test-model");
        await runtime.ExecuteTurnAsync(
            "wraith1", first.Run.RunId, privateText, CancellationToken.None);
        var second = await runtime.StartRunAsync(
            "wraith2", null, "Remain isolated", "capture", "test-model");
        await runtime.ExecuteTurnAsync(
            "wraith2", second.Run.RunId, "Begin without foreign context.", CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("wraith1", provider.Requests[0].Identity.Name);
        Assert.Equal("wraith2", provider.Requests[1].Identity.Name);
        Assert.DoesNotContain(
            privateText,
            JsonSerializer.Serialize(provider.Requests[1]),
            StringComparison.Ordinal);
        var secondArchive = await archive.ReadAllAsync(
            Deckwraith.Core.Naming.CanonicalName.Parse("wraith2"), CancellationToken.None);
        Assert.All(secondArchive, record =>
            Assert.DoesNotContain(privateText, record.Payload.GetRawText(), StringComparison.Ordinal));
        Assert.Equal(string.Empty, await StateSpineEndToEndTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"], CancellationToken.None));
    }

    [Fact]
    public async Task ATerminalRunReopensTheSingleActiveRunSlot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            new FixedClock()))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        using var runtime = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([new CapturingProvider()]),
            clock: new FixedClock());
        var first = await runtime.StartRunAsync(
            "wraith1", null, "First objective", "capture", "test-model");

        var conflict = await Assert.ThrowsAsync<Deckwraith.Core.State.DeckStateException>(() =>
            runtime.StartRunAsync(
                "wraith1", null, "Conflicting objective", "capture", "test-model"));
        Assert.Contains(first.Run.RunId, conflict.Message, StringComparison.Ordinal);

        using var lifecycle = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            new FixedClock());
        var archiveConflict = await Assert.ThrowsAsync<Deckwraith.Core.State.DeckStateException>(() =>
            lifecycle.ArchiveWraithAsync("wraith1", CancellationToken.None));
        Assert.Contains(first.Run.RunId, archiveConflict.Message, StringComparison.Ordinal);

        await runtime.CompleteRunAsync(
            "wraith1", first.Run.RunId, "first objective complete", CancellationToken.None);
        var archived = await lifecycle.ArchiveWraithAsync("wraith1", CancellationToken.None);
        Assert.NotNull(archived.Value.ArchivedAt);
        var archivedConflict = await Assert.ThrowsAsync<Deckwraith.Core.State.DeckStateException>(() =>
            runtime.StartRunAsync(
                "wraith1", null, "Archived objective", "capture", "test-model"));
        Assert.Contains("must be restored", archivedConflict.Message, StringComparison.Ordinal);
        var restored = await lifecycle.RestoreWraithAsync("wraith1", CancellationToken.None);
        Assert.Null(restored.Value.ArchivedAt);
        var second = await runtime.StartRunAsync(
            "wraith1", null, "Second objective", "capture", "test-model");

        Assert.NotEqual(first.Run.RunId, second.Run.RunId);
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
                Assert.Equal("replacement-model", request.Model);
                Assert.Contains(request.Context.Items, item => item.Kind is ContextItemKind.ToolElision);
                yield return new ModelTextDelta("second turn");
                yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FailingProvider : IModelProvider
    {
        public string ProviderId => "failing";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseStarted("failed-request");
            yield return new ModelProviderError(
                "intentional-failure", "provider failed intentionally", false);
        }
    }

    private sealed class CapturingProvider : IModelProvider
    {
        public string ProviderId => "capture";

        public ProviderCapabilities Capabilities { get; } = new(
            true, false, false, false, false);

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelResponseStarted($"capture-{Requests.Count}");
            yield return new ModelTextDelta("isolated");
            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
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
