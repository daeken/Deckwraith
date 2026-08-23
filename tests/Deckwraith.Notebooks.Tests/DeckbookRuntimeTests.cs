using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.Notebooks.Tests;

public sealed class DeckbookRuntimeTests
{
    private static readonly string[] LinearCellNames = ["one", "two", "three"];

    [Fact]
    public async Task StructuralEditsUseSparseOrderAndInvalidateOnlyTheLinearSuffix()
    {
        using var environment = await TestEnvironment.CreateAsync();
        await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell("intro", DeckbookCellKind.Markdown, "# Intro"));
        var load = await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell("load", DeckbookCellKind.Code, "'load'", "powershell"));
        var report = await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell("report", DeckbookCellKind.Code, "'report'", "powershell"));
        var transform = await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "transform",
                DeckbookCellKind.Code,
                "'transform'",
                "powershell",
                Before: "report"));

        Assert.Equal(2_048, load.Cell.Position);
        Assert.Equal(3_072, report.Cell.Position);
        Assert.Equal(2_560, transform.Cell.Position);
        Assert.Equal(0, environment.Kernel.InvocationCount);

        var initialRun = await environment.Runtime.RunRemainingAsync(
            "wraith1", "deckwraith", "load");
        Assert.True(initialRun.Completed);
        Assert.Equal(3, environment.Kernel.InvocationCount);
        var fresh = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        Assert.All(fresh.Cells.Where(cell => cell.Cell.IsExecutable), cell => Assert.False(cell.Cell.IsStale));

        await environment.Runtime.EditAsync(
            "wraith1", "deckwraith", "transform", "'transform-v2'");
        var edited = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        Assert.False(Cell(edited, "load").Cell.IsStale);
        Assert.True(Cell(edited, "transform").Cell.IsStale);
        Assert.True(Cell(edited, "report").Cell.IsStale);
        Assert.Equal(3, environment.Kernel.InvocationCount);

        await environment.Runtime.RunCellAsync("wraith1", "deckwraith", "transform");
        var partial = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        Assert.False(Cell(partial, "transform").Cell.IsStale);
        Assert.True(Cell(partial, "report").Cell.IsStale);

        await environment.Runtime.RenameAsync(
            "wraith1", "deckwraith", "load", "load-data");
        var renamedByAlias = await environment.Runtime.EditAsync(
            "wraith1", "deckwraith", "load", "'load-v2'");
        Assert.Equal("load-data", renamedByAlias.Cell.Name);

        await environment.Runtime.MoveAsync(
            "wraith1", "deckwraith", "report", before: "load-data");
        var moved = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        Assert.Equal(
            ["intro", "report", "load-data", "transform"],
            moved.Cells.Select(cell => cell.Cell.Name));
        Assert.True(Cell(moved, "report").Cell.IsStale);
        Assert.True(Cell(moved, "load-data").Cell.IsStale);
        Assert.True(Cell(moved, "transform").Cell.IsStale);

        await environment.Runtime.RunRemainingAsync(
            "wraith1", "deckwraith", "report");
        var beforeDelete = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        var retainedHash = Cell(beforeDelete, "load-data").Cell.LastExecution?.OutputHash;
        Assert.NotNull(retainedHash);
        var afterDelete = await environment.Runtime.DeleteAsync(
            "wraith1", "deckwraith", "load-data");
        Assert.False(Cell(afterDelete, "report").Cell.IsStale);
        Assert.True(Cell(afterDelete, "transform").Cell.IsStale);
        Assert.True(File.Exists(System.IO.Path.Combine(
            environment.Path,
            "agents",
            "wraith1",
            "deckbooks",
            "deckwraith",
            "outputs",
            retainedHash[7..] + ".json")));
        await Assert.ThrowsAsync<DeckStateException>(() => environment.Runtime.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell("load", DeckbookCellKind.Code, "'reuse'", "powershell")));
    }

    [Fact]
    public async Task RunRemainingStopsAtFailureAndRetainsLaterOutput()
    {
        using var environment = await TestEnvironment.CreateAsync();
        foreach (var name in LinearCellNames)
        {
            await environment.Runtime.InsertAsync(
                "wraith1", "deckwraith",
                new InsertDeckbookCell(name, DeckbookCellKind.Code, $"'{name}'", "powershell"));
        }

        var first = await environment.Runtime.RunRemainingAsync(
            "wraith1", "deckwraith", "one");
        Assert.True(first.Completed);
        var beforeFailure = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        var retainedOutput = Cell(beforeFailure, "three").Cell.LastExecution?.OutputHash;
        Assert.NotNull(retainedOutput);

        await environment.Runtime.EditAsync(
            "wraith1", "deckwraith", "two", "FAIL");
        var invocationCount = environment.Kernel.InvocationCount;
        var failed = await environment.Runtime.RunRemainingAsync(
            "wraith1", "deckwraith", "two");

        Assert.False(failed.Completed);
        Assert.Equal("two", failed.StoppedAt);
        Assert.Single(failed.Executions);
        Assert.Equal(invocationCount + 1, environment.Kernel.InvocationCount);
        var afterFailure = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        Assert.True(Cell(afterFailure, "two").Cell.IsStale);
        Assert.True(Cell(afterFailure, "three").Cell.IsStale);
        Assert.Equal(retainedOutput, Cell(afterFailure, "three").Cell.LastExecution?.OutputHash);
        Assert.NotNull(Cell(afterFailure, "three").Output);
    }

    [Fact]
    public async Task ContextProjectionIncludesPinsAndActiveWindowButExcludesUnrelatedLargeCells()
    {
        using var environment = await TestEnvironment.CreateAsync();
        await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "pinned",
                DeckbookCellKind.Markdown,
                "durable fact",
                ContextPolicy: CellContextPolicy.Pinned,
                Synopsis: "always included"));
        await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "unrelated",
                DeckbookCellKind.Markdown,
                new string('x', 20_000),
                Synopsis: "large but merely indexed"));
        await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "previous",
                DeckbookCellKind.Markdown,
                "nearby context",
                Synopsis: "near active"));
        await environment.Runtime.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "active",
                DeckbookCellKind.Code,
                "'active'",
                "powershell",
                Synopsis: "current work"));

        var projection = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", "active", precedingWindow: 1, maximumCharacters: 1_200);
        var repeated = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", "active", precedingWindow: 1, maximumCharacters: 1_200);

        Assert.Equal(["pinned", "previous", "active"], projection.IncludedCells.Select(cell => cell.Name));
        Assert.DoesNotContain(projection.IncludedCells, cell => cell.Name == "unrelated");
        Assert.Contains(projection.Index, cell => cell.Name == "unrelated");
        Assert.True(CanonicalJson.Serialize(projection).Length <= 1_200);
        Assert.Equal(projection.ProjectionHash, repeated.ProjectionHash);
        Assert.Equal(0, environment.Kernel.InvocationCount);
        Assert.Equal(string.Empty, await RunGitAsync(
            environment.Path, ["status", "--porcelain"]));
    }

    [Fact]
    public async Task ContextPolicyCanPinAndUnpinWithoutExecutingCells()
    {
        using var environment = await TestEnvironment.CreateAsync();
        await environment.Runtime.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "mutable-pin",
                DeckbookCellKind.Code,
                "'never executed'",
                "powershell"));

        var unpinned = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", activeCell: null);
        Assert.DoesNotContain(unpinned.IncludedCells, cell => cell.Name == "mutable-pin");

        var pinnedSnapshot = await environment.Runtime.SetContextPolicyAsync(
            "wraith1", "deckwraith", "mutable-pin", CellContextPolicy.Pinned);
        Assert.Equal(CellContextPolicy.Pinned, Cell(pinnedSnapshot, "mutable-pin").Cell.ContextPolicy);
        var pinned = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", activeCell: null);
        Assert.Contains(pinned.IncludedCells, cell => cell.Name == "mutable-pin");

        var unpinnedSnapshot = await environment.Runtime.SetContextPolicyAsync(
            "wraith1", "deckwraith", "mutable-pin", CellContextPolicy.Never);
        Assert.Equal(CellContextPolicy.Never, Cell(unpinnedSnapshot, "mutable-pin").Cell.ContextPolicy);
        var removed = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", activeCell: null);
        Assert.DoesNotContain(removed.IncludedCells, cell => cell.Name == "mutable-pin");
        Assert.Equal(0, environment.Kernel.InvocationCount);
    }

    [Fact]
    public async Task ReadingAndCompilingAnEmptyDeckbookDoesNotCreateState()
    {
        using var environment = await TestEnvironment.CreateAsync();
        var snapshot = await environment.Runtime.GetAsync("wraith1", "deckwraith");
        var projection = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", activeCell: null);
        var repeated = await environment.Runtime.CompileContextAsync(
            "wraith1", "deckwraith", activeCell: null);

        Assert.Empty(snapshot.Cells);
        Assert.Empty(projection.IncludedCells);
        Assert.Empty(projection.Index);
        Assert.Equal(projection.ProjectionHash, repeated.ProjectionHash);
        Assert.False(Directory.Exists(System.IO.Path.Combine(
            environment.Path,
            "agents",
            "wraith1",
            "deckbooks",
            "deckwraith")));
        Assert.Equal(string.Empty, await RunGitAsync(
            environment.Path, ["status", "--porcelain"]));
    }

    private static DeckbookCellView Cell(DeckbookSnapshot snapshot, string name) =>
        Assert.Single(snapshot.Cells, cell => cell.Cell.Name == name);

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

    private sealed class RecordingKernel : ICellKernel
    {
        public string KernelId => "powershell";

        public KernelCapabilities Capabilities { get; } = new(true, true, true, true);

        public int InvocationCount { get; private set; }

        public async IAsyncEnumerable<CellKernelEvent> ExecuteAsync(
            CellExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            InvocationCount++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new CellKernelStarted("fake-1.0", 1);
            if (request.Source.Contains("FAIL", StringComparison.Ordinal))
            {
                yield return new CellKernelErrorProduced("fake.failure", "requested failure");
                yield return new CellKernelCompleted(CellKernelExecutionStatus.Failed);
                yield break;
            }

            yield return new CellKernelValueProduced(CanonicalJson.ToElement(new
            {
                request.CellName,
                request.Source,
                InvocationCount,
            }));
            yield return new CellKernelCompleted(CellKernelExecutionStatus.Succeeded);
        }

        public Task InterruptAsync(string executionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(
            string path,
            DeckbookRuntime runtime,
            RecordingKernel kernel)
        {
            Path = path;
            Runtime = runtime;
            Kernel = kernel;
        }

        public string Path { get; }

        public DeckbookRuntime Runtime { get; }

        public RecordingKernel Kernel { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"deckwraith-notebook-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            var stateStore = new JsonDeckStateStore(path);
            var archive = new JsonlAgentArchive(path);
            var checkpoints = new GitCheckpointStore(path);
            using (var state = new StateSpine(
                stateStore,
                archive,
                new ContentAddressedArtifactStore(path),
                checkpoints,
                new FixedClock()))
            {
                await state.InitializeAsync(CancellationToken.None);
                await state.CreateHauntAsync("deckwraith", CancellationToken.None);
                await state.CreateWraithAsync("wraith1", CancellationToken.None);
            }

            var kernel = new RecordingKernel();
            var runtime = new DeckbookRuntime(
                path,
                stateStore,
                new CellKernelRegistry([kernel]),
                archive,
                checkpoints,
                new FixedClock());
            return new TestEnvironment(path, runtime, kernel);
        }

        public void Dispose()
        {
            Runtime.Dispose();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FixedClock : IDeckClock
    {
        private long _ticks;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddTicks(
            Interlocked.Increment(ref _ticks));
    }
}
