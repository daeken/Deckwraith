using System.Diagnostics;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.IntegrationTests;

public sealed class DurableStateEndToEndTests
{
    private const string RunId = "00000000000000000000000000000001";
    private static readonly string[] AlphaLabels = ["alpha"];
    private static readonly string[] AlphaBetaLabels = ["alpha", "beta"];
    private static readonly string[] ContendedWinners = ["first", "second"];

    [Fact]
    public async Task ScopedValuesSurviveColdClientsAndEnforceCompareAndSwap()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var clock = new FixedClock();
        using (var state = new StateSpine(
            deckState,
            archive,
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            checkpoints,
            clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateHauntAsync("deckwraith", CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var run = new RunDocument(
            RunDocument.CurrentSchemaVersion,
            RunId,
            "wraith1",
            "deckwraith",
            "Exercise durable state",
            RunStatus.Created,
            null,
            [new ShellDocument(
                "shell-1", "fake", "test-model", clock.UtcNow, null, null)],
            clock.UtcNow,
            clock.UtcNow);
        await inferenceState.CreateRunAsync(
            CanonicalName.Parse("wraith1"), run, CancellationToken.None);
        await checkpoints.CheckpointAsync(
            "test-run-created",
            CanonicalName.Parse("wraith1"),
            CanonicalName.Parse("deckwraith"),
            CancellationToken.None);

        var values = new JsonDurableValueStore(temporaryDirectory.Path);
        var runtime = new DurableStateRuntime(deckState, values, archive, checkpoints, clock);
        var created = await runtime.SetAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            CanonicalJson.ToElement(new { count = 1, labels = AlphaLabels }),
            expectedVersion: 0,
            cancellationToken: CancellationToken.None);
        Assert.Equal(1, created.Value?.Version);

        var updated = await runtime.SetAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            CanonicalJson.ToElement(new { count = 2, labels = AlphaBetaLabels }),
            expectedVersion: 1,
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, updated.Value?.Version);
        await Assert.ThrowsAsync<DeckStateException>(() => runtime.SetAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            CanonicalJson.ToElement(new { count = 3 }),
            expectedVersion: 1,
            cancellationToken: CancellationToken.None));

        await runtime.SetAsync(
            "wraith1",
            DurableValueScope.Run,
            "current-targets",
            CanonicalJson.ToElement("run-local"),
            runId: RunId,
            expectedVersion: 0,
            cancellationToken: CancellationToken.None);
        await runtime.SetAsync(
            "wraith1",
            DurableValueScope.Haunt,
            "current-targets",
            CanonicalJson.ToElement("shared"),
            haunt: "deckwraith",
            expectedVersion: 0,
            cancellationToken: CancellationToken.None);

        var coldRuntime = new DurableStateRuntime(
            new JsonDeckStateStore(temporaryDirectory.Path),
            new JsonDurableValueStore(temporaryDirectory.Path),
            new JsonlAgentArchive(temporaryDirectory.Path),
            new GitCheckpointStore(temporaryDirectory.Path),
            clock);
        var survived = await coldRuntime.GetAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, survived?.Version);
        Assert.Equal(2, survived?.Value.GetProperty("count").GetInt32());
        Assert.Single(await coldRuntime.ListAsync(
            "wraith1",
            DurableValueScope.Run,
            RunId,
            cancellationToken: CancellationToken.None));

        var removed = await coldRuntime.RemoveAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            expectedVersion: 2,
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, removed.Value?.Version);
        Assert.Null(await coldRuntime.GetAsync(
            "wraith1",
            DurableValueScope.Agent,
            "current-targets",
            cancellationToken: CancellationToken.None));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(4, records.Count(record => record.Kind == "state.value-written"));
        Assert.Single(records, record => record.Kind == "state.value-removed");
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    [Fact]
    public async Task CompareAndSwapSerializesIndependentStoreInstances()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState,
            new JsonlAgentArchive(temporaryDirectory.Path),
            new ContentAddressedArtifactStore(temporaryDirectory.Path),
            new GitCheckpointStore(temporaryDirectory.Path),
            new FixedClock()))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var wraith = CanonicalName.Parse("wraith1");
        var firstStore = new JsonDurableValueStore(temporaryDirectory.Path);
        var secondStore = new JsonDurableValueStore(temporaryDirectory.Path);
        await firstStore.WriteAsync(
            wraith,
            DurableValueScope.Agent,
            "contended",
            CanonicalJson.ToElement("initial"),
            runId: null,
            haunt: null,
            expectedVersion: 0,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        using var ready = new CountdownEvent(2);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var successes = 0;
        var conflicts = 0;

        async Task CompeteAsync(JsonDurableValueStore store, string value)
        {
            ready.Signal();
            await release.Task;
            try
            {
                await store.WriteAsync(
                    wraith,
                    DurableValueScope.Agent,
                    "contended",
                    CanonicalJson.ToElement(value),
                    runId: null,
                    haunt: null,
                    expectedVersion: 1,
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    CancellationToken.None);
                Interlocked.Increment(ref successes);
            }
            catch (DeckStateException exception) when (
                exception.Message.Contains("version conflict", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref conflicts);
            }
        }

        var first = Task.Run(() => CompeteAsync(firstStore, "first"));
        var second = Task.Run(() => CompeteAsync(secondStore, "second"));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, successes);
        Assert.Equal(1, conflicts);
        var final = await firstStore.ReadAsync(
            wraith,
            DurableValueScope.Agent,
            "contended",
            runId: null,
            haunt: null,
            CancellationToken.None);
        Assert.Equal(2, final?.Version);
        Assert.Contains(final?.Value.GetString(), ContendedWinners);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string[] arguments)
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
                System.IO.Path.GetTempPath(), $"deckwraith-durable-state-{Guid.NewGuid():N}");
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
