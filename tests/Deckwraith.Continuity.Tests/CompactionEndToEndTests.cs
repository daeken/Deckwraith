using System.Runtime.CompilerServices;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Continuity;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Continuity.Tests;

public sealed class CompactionEndToEndTests
{
    [Fact]
    public async Task OldestContiguousPrefixUsesIndependentModelAndPreservesRawState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clock = new FixedClock();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var inferenceState = new JsonInferenceStateStore(temporaryDirectory.Path);
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

        var deckbookSentinel = Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "deckbooks", "sentinel.txt");
        await File.WriteAllTextAsync(deckbookSentinel, "deckbook-must-not-change");
        await checkpoints.CheckpointAsync(
            "test-deckbook-sentinel", CanonicalName.Parse("wraith1"), null, CancellationToken.None);

        var activeProvider = new EchoProvider();
        using (var inference = new InferenceRuntime(
            deckState,
            inferenceState,
            archive,
            checkpoints,
            new ModelProviderRegistry([activeProvider]),
            clock: clock))
        {
            var run = await inference.StartRunAsync(
                "wraith1", null, "Accumulate history", "active", "active-model");
            for (var turn = 1; turn <= 5; turn++)
            {
                _ = await inference.ExecuteTurnAsync(
                    "wraith1", run.Run.RunId, $"user-turn-{turn}");
            }
        }

        var recordsBefore = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        var rawHashesBefore = recordsBefore.Select(record => record.ContentHash).ToArray();
        var contextBefore = await inferenceState.ReadContextAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        var compactor = new RecordingCompactor();
        var runtime = new CompactionRuntime(
            deckState,
            inferenceState,
            archive,
            new JsonCompactionStore(temporaryDirectory.Path),
            checkpoints,
            new ModelProviderRegistry([compactor]),
            clock);

        var result = await runtime.CompactAsync(
            "wraith1", "compactor", "summary-model", fraction: 0.45, minimumRecords: 8);

        Assert.NotNull(result);
        Assert.Equal(1, result.Compaction.FirstSequence);
        Assert.True(result.Compaction.LastSequence < recordsBefore[^1].Sequence);
        Assert.Equal("compactor", result.Compaction.Provider);
        Assert.Equal("summary-model", result.Compaction.Model);
        Assert.Equal(CompactionRuntime.PromptVersion, result.Compaction.PromptVersion);
        Assert.Equal("Old decisions and failures, faithfully summarized.", result.Compaction.Summary);
        Assert.Equal(["resolve the parser ambiguity"], result.Compaction.UnresolvedItems);
        Assert.Equal(["sha256:artifact"], result.Compaction.ArtifactReferences);
        Assert.NotNull(result.Compaction.CheckpointCommit);
        Assert.Equal(1, compactor.Invocations);
        Assert.Equal("summary-model", compactor.SeenModel);
        Assert.True(result.Context.Items.Count < contextBefore.Items.Count);
        var summary = Assert.Single(
            result.Context.Items, item => item.Kind is ContextItemKind.Compaction);
        Assert.Equal(result.Compaction.CompactionId, summary.ItemId);
        Assert.Equal(result.Compaction.FirstSequence, summary.ArchiveFirstSequence);
        Assert.Equal(result.Compaction.LastSequence, summary.ArchiveLastSequence);
        Assert.Contains(result.Context.Items, item => item.Text == "user-turn-5");

        var recordsAfter = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(
            rawHashesBefore,
            recordsAfter.Take(recordsBefore.Count).Select(record => record.ContentHash).ToArray());
        CompactionCoverage.ValidateExisting([result.Compaction], recordsAfter);
        CompactionRuntime.ValidateSource(result.Compaction, recordsAfter);
        var rebuilt = ContextArchiveRebuilder.Rebuild(
            CanonicalName.Parse("wraith1"),
            recordsAfter,
            result.Context.IdentityHash,
            result.Context.ToolElisionTurns,
            clock.UtcNow);
        Assert.Equal(CanonicalJson.Hash(result.Context), CanonicalJson.Hash(rebuilt));
        Assert.Equal("deckbook-must-not-change", await File.ReadAllTextAsync(deckbookSentinel));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(startInfo) ??
            throw new InvalidOperationException();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class EchoProvider : IModelProvider
    {
        public string ProviderId => "active";

        public ProviderCapabilities Capabilities { get; } =
            new(true, false, false, false, false);

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseStarted(request.RequestId);
            yield return new ModelTextDelta("assistant-response");
            yield return new ModelResponseCompleted(ModelFinishReason.Stop, null);
        }
    }

    private sealed class RecordingCompactor : IModelProvider
    {
        public string ProviderId => "compactor";

        public ProviderCapabilities Capabilities { get; } =
            new(true, false, false, false, false);

        public int Invocations { get; private set; }

        public string? SeenModel { get; private set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            SeenModel = request.Model;
            Assert.Empty(request.Tools);
            Assert.Contains("Preserve decisions", request.Objective, StringComparison.Ordinal);
            Assert.Contains("Canonical source records", request.Objective, StringComparison.Ordinal);
            yield return new ModelResponseStarted(request.RequestId);
            yield return new ModelTextDelta(
                """{"summary":"Old decisions and failures, faithfully summarized.","unresolvedItems":["resolve the parser ambiguity"],"artifactReferences":["sha256:artifact"]}""");
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
                System.IO.Path.GetTempPath(), $"deckwraith-compaction-{Guid.NewGuid():N}");
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
