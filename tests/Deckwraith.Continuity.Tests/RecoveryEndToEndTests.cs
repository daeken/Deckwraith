using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Continuity;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;
using Deckwraith.Mcp;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Continuity.Tests;

public sealed class RecoveryEndToEndTests
{
    [Fact]
    public async Task CrashRecoveryMarksUnknownRebuildsProjectionAndRollsShellCold()
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

        RunStartResult started;
        using (var inference = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([new EchoProvider()]),
            clock: clock))
        {
            started = await inference.StartRunAsync(
                "wraith1", null, "Recover me", "fake", "active-model");
            _ = await inference.ExecuteTurnAsync(
                "wraith1", started.Run.RunId, "committed-user");
        }

        await archive.AppendAsync(
            new ArchiveEvent(
                "wraith1",
                "message.user",
                CanonicalJson.ToElement(new { text = "unprojected-user" }),
                RunId: started.Run.RunId,
                Timestamp: clock.UtcNow),
            CancellationToken.None);
        const string projectedOperation = "completed-not-projected";
        await archive.AppendAsync(
            new ArchiveEvent(
                "wraith1",
                "tool.started",
                CanonicalJson.ToElement(new
                {
                    operationId = projectedOperation,
                    callId = "call-not-projected",
                    name = "Recovered-Tool",
                    arguments = new { value = 7 },
                }),
                RunId: started.Run.RunId,
                EventId: projectedOperation,
                Timestamp: clock.UtcNow),
            CancellationToken.None);
        await archive.AppendAsync(
            new ArchiveEvent(
                "wraith1",
                "tool.completed",
                CanonicalJson.ToElement(new
                {
                    operationId = projectedOperation,
                    output = new { accepted = true },
                }),
                RunId: started.Run.RunId,
                Timestamp: clock.UtcNow),
            CancellationToken.None);
        await archive.AppendAsync(
            new ArchiveEvent(
                "wraith1",
                "message.assistant",
                CanonicalJson.ToElement(new { text = "unprojected-assistant" }),
                RunId: started.Run.RunId,
                Timestamp: clock.UtcNow),
            CancellationToken.None);

        var marker = Path.Combine(temporaryDirectory.Path, "must-not-replay.txt");
        var serverAssembly = Path.Combine(
            AppContext.BaseDirectory, "Deckwraith.Mcp.TestServer.dll");
        using (var mcp = new McpCatalogRuntime(
            temporaryDirectory.Path,
            deckState,
            archive,
            checkpoints,
            clock,
            new ThrowingCrashInjector()))
        {
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
            await Assert.ThrowsAsync<McpCrashInjectionException>(() => mcp.CallToolAsync(
                "fake/emit_structured_side_effect",
                JsonSerializer.SerializeToElement(new { label = "never-replay", count = 9 }),
                new McpInvocationContext(
                    "wraith1", null, started.Run.RunId, "shell-crash", "mcp-crash-1")));
        }

        Assert.False(File.Exists(marker));
        var crashedRun = await inferenceState.ReadRunAsync(
            CanonicalName.Parse("wraith1"), started.Run.RunId, CancellationToken.None);
        await inferenceState.WriteRunAsync(
            CanonicalName.Parse("wraith1"),
            crashedRun with
            {
                Status = RunStatus.Running,
                StatusReason = null,
                UpdatedAt = clock.UtcNow,
            },
            CancellationToken.None);
        var contextPath = Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "context.json");
        await File.WriteAllTextAsync(contextPath, "{ crash-corrupted-context");

        var recovery = new RecoveryRuntime(
            temporaryDirectory.Path,
            deckState,
            inferenceState,
            archive,
            new JsonCompactionStore(temporaryDirectory.Path),
            checkpoints,
            clock);
        var result = await recovery.RecoverAsync("wraith1");

        Assert.NotNull(result.Incident);
        Assert.Equal(["mcp-crash-1"], result.Incident.OutcomeUnknownOperationIds);
        Assert.True(result.Incident.ContextRebuilt);
        Assert.Equal([started.Run.RunId], result.Incident.RecoveredRunIds);
        Assert.Contains(result.Context.Items, item => item.Text == "unprojected-user");
        Assert.Contains(result.Context.Items, item => item.Text == "unprojected-assistant");
        var replayed = Assert.Single(result.Context.Items, item =>
            item.OperationId == projectedOperation);
        Assert.Equal(OperationStatus.Completed, replayed.Status);
        Assert.True(replayed.Output!.Value.GetProperty("accepted").GetBoolean());
        Assert.Equal(2, result.Context.Turn);
        var persistedContext = await inferenceState.ReadContextAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(CanonicalJson.Hash(result.Context), CanonicalJson.Hash(persistedContext));

        var recoveredRun = Assert.Single(result.Runs);
        Assert.Equal(RunStatus.AwaitingInput, recoveredRun.Status);
        Assert.Equal("startup-recovered-cold-shell", recoveredRun.StatusReason);
        Assert.Equal(2, recoveredRun.Shells.Count);
        Assert.Equal(
            "startup-recovery-outcome-unknown",
            recoveredRun.Shells[0].EndReason);
        Assert.Null(recoveredRun.Shells[1].EndedAt);
        Assert.False(File.Exists(marker));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        var unknown = Assert.Single(records, record => record.Kind == "mcp.outcome-unknown");
        Assert.Equal("mcp-crash-1", unknown.Payload.GetProperty("operationId").GetString());
        Assert.False(unknown.Payload.GetProperty("replayed").GetBoolean());
        Assert.Single(records, record => record.Kind == "run.recovered");
        Assert.Single(records, record => record.Kind == "recovery.completed");
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(temporaryDirectory.Path, "recovery", "incidents"), "*.json"));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));

        var second = await recovery.RecoverAsync("wraith1");
        Assert.Null(second.Incident);
        Assert.Equal(CanonicalJson.Hash(result.Context), CanonicalJson.Hash(second.Context));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ReversalCreatesRecoveryBranchAndNewInverseCommit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clock = new FixedClock();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        string badCommit;
        using (var mcp = new McpCatalogRuntime(
            temporaryDirectory.Path, deckState, archive, checkpoints, clock))
        {
            badCommit = await mcp.WriteWraithAssignmentAsync(
                "wraith1",
                new McpAssignmentDocument(
                    McpAssignmentDocument.CurrentSchemaVersion,
                    ["bad-server"],
                    [],
                    [],
                    [],
                    clock.UtcNow));
        }

        var assignmentPath = Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "tools", "mcp.json");
        Assert.True(File.Exists(assignmentPath));
        var reversal = await new GitReversalRuntime(
            temporaryDirectory.Path, checkpoints, clock).ReverseCommitAsync(badCommit);

        Assert.Equal(badCommit, reversal.ReversedCommit);
        Assert.NotEqual(reversal.PreviousHead, reversal.NewHead);
        Assert.False(File.Exists(assignmentPath));
        Assert.Contains("External side effects", reversal.Warning, StringComparison.Ordinal);
        Assert.Equal(
            reversal.PreviousHead,
            (await RunGitAsync(
                temporaryDirectory.Path, ["rev-parse", reversal.NewHead + "^"])).Trim());
        Assert.NotEmpty(await RunGitAsync(
            temporaryDirectory.Path,
            ["show-ref", "--verify", "refs/heads/" + reversal.RecoveryBranch]));
        Assert.NotEmpty(await RunGitAsync(
            temporaryDirectory.Path, ["cat-file", "-t", badCommit]));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class ThrowingCrashInjector : IMcpCrashInjector
    {
        public void Inject(string point, string operationId) =>
            throw new McpCrashInjectionException(point, operationId);
    }

    private sealed class EchoProvider : IModelProvider
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } =
            new(true, false, false, false, false);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseStarted(request.RequestId);
            yield return new ModelTextDelta("committed-assistant");
            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
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
                System.IO.Path.GetTempPath(), $"deckwraith-recovery-{Guid.NewGuid():N}");
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
