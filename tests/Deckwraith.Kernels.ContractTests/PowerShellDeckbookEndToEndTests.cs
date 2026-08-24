using System.Diagnostics;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Serialization;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.Kernels.ContractTests;

public sealed class PowerShellDeckbookEndToEndTests
{
    [Fact]
    public async Task PowerShellCellsShareAmbientStateStopOnFailureAndSurviveColdReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var clock = new FixedClock();
        using (var state = new StateSpine(
            deckState,
            archive,
            artifactStore,
            checkpoints,
            clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateHauntAsync("deckwraith", CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        var artifactRuntime = new ArtifactRuntime(
            deckState, artifactStore, archive, checkpoints, clock);
        using var runspaces = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            durableState,
            artifactRuntime,
            archive,
            checkpoints,
            clock);
        using var kernel = new PowerShellCellKernel(runspaces);
        using var notebooks = new DeckbookRuntime(
            temporaryDirectory.Path,
            deckState,
            new CellKernelRegistry([kernel]),
            archive,
            checkpoints,
            clock);
        var markerPath = Path.Combine(temporaryDirectory.Path, "later-ran.txt");

        await notebooks.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "set-state",
                DeckbookCellKind.Code,
                "$global:shared = 40; $shared",
                "powershell"));
        await notebooks.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "use-state",
                DeckbookCellKind.Code,
                "$global:shared += [int]$DwCellInput.increment; " +
                "[pscustomobject]@{ shared = $shared; input = [int]$DwCellInput.increment }",
                "powershell"));
        await notebooks.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "stop-here",
                DeckbookCellKind.Code,
                "Write-Error -ErrorId 'cell.stop' 'stop here'",
                "powershell"));
        await notebooks.InsertAsync(
            "wraith1", "deckwraith",
            new InsertDeckbookCell(
                "later",
                DeckbookCellKind.Code,
                $"Set-Content -LiteralPath {Quote(markerPath)} -Value 'ran'; 'later'",
                "powershell"));

        var failed = await notebooks.RunRemainingAsync(
            "wraith1",
            "deckwraith",
            "set-state",
            input: CanonicalJson.ToElement(new { increment = 2 }));

        Assert.False(failed.Completed);
        Assert.Equal("stop-here", failed.StoppedAt);
        Assert.Equal(3, failed.Executions.Count);
        Assert.Equal(
            CellKernelExecutionStatus.Succeeded,
            failed.Executions[0].Output.Status);
        Assert.Equal(
            42,
            failed.Executions[1].Output.Values[^1].GetProperty("shared").GetInt32());
        Assert.Equal(
            CellKernelExecutionStatus.Failed,
            failed.Executions[2].Output.Status);
        Assert.Contains(failed.Executions[2].Output.Errors, error => error.Contains("cell.stop", StringComparison.Ordinal));
        Assert.False(File.Exists(markerPath));
        Assert.Equal("powershell", failed.Executions[1].Cell.Cell.LastExecution?.Kernel);
        Assert.NotEqual("unknown", failed.Executions[1].Cell.Cell.LastExecution?.KernelVersion);
        Assert.Equal(1, failed.Executions[1].Cell.Cell.LastExecution?.KernelEpoch);

        await notebooks.EditAsync(
            "wraith1", "deckwraith", "stop-here", "$global:shared += 1; $shared");
        var resumed = await notebooks.RunRemainingAsync(
            "wraith1", "deckwraith", "stop-here");
        Assert.True(resumed.Completed);
        Assert.Equal(2, resumed.Executions.Count);
        Assert.True(File.Exists(markerPath));

        var beforeReplacement = await notebooks.GetAsync("wraith1", "deckwraith");
        var oldOutputHash = Assert.Single(
            beforeReplacement.Cells,
            cell => cell.Cell.Name == "use-state").Cell.LastExecution?.OutputHash;
        Assert.NotNull(oldOutputHash);
        await runspaces.ReplaceAsync(
            new PowerShellInvocationContext("wraith1", null, "deckwraith"),
            "kernel-loss-test",
            CancellationToken.None);
        var afterReplacement = await notebooks.RunCellAsync(
            "wraith1",
            "deckwraith",
            "use-state",
            input: CanonicalJson.ToElement(new { increment = 1 }));
        Assert.Equal(
            1,
            afterReplacement.Output.Values[^1].GetProperty("shared").GetInt32());
        Assert.Equal(2, afterReplacement.Cell.Cell.LastExecution?.KernelEpoch);
        Assert.NotEqual(oldOutputHash, afterReplacement.Output.Hash);
        Assert.True(File.Exists(Path.Combine(
            temporaryDirectory.Path,
            "agents",
            "wraith1",
            "deckbooks",
            "deckwraith",
            "outputs",
            oldOutputHash[7..] + ".json")));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    [Fact]
    public async Task PowerShellKernelInterruptStopsAnActiveExecutionAsCancelled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var clock = new FixedClock();
        using (var state = new StateSpine(
            deckState,
            archive,
            artifactStore,
            checkpoints,
            clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateHauntAsync("deckwraith", CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        var artifactRuntime = new ArtifactRuntime(
            deckState, artifactStore, archive, checkpoints, clock);
        using var runspaces = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            durableState,
            artifactRuntime,
            archive,
            checkpoints,
            clock);
        using var kernel = new PowerShellCellKernel(runspaces);
        var markerPath = Path.Combine(
            Path.GetTempPath(), $"deckwraith-interrupt-{Guid.NewGuid():N}.txt");
        var executionId = Guid.CreateVersion7().ToString("N");
        try
        {
            var pending = Task.Run(async () =>
            {
                var events = new List<CellKernelEvent>();
                await foreach (var kernelEvent in kernel.ExecuteAsync(
                    new CellExecutionRequest(
                        executionId,
                        "wraith1",
                        RunId: null,
                        "deckwraith",
                        "interrupt-me",
                        $"Set-Content -LiteralPath {Quote(markerPath)} -Value 'started'; " +
                        "while ($true) { Start-Sleep -Milliseconds 25 }",
                        CanonicalJson.ToElement<object?>(null)),
                    CancellationToken.None))
                {
                    events.Add(kernelEvent);
                }

                return events;
            });

            await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(5));
            await kernel.InterruptAsync(executionId, CancellationToken.None);
            var events = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            var started = Assert.IsType<CellKernelStarted>(events[0]);
            Assert.True(started.KernelEpoch > 0);
            var completed = Assert.IsType<CellKernelCompleted>(events[^1]);
            Assert.Equal(CellKernelExecutionStatus.Cancelled, completed.Status);
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(20, cancellation.Token);
        }
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
                System.IO.Path.GetTempPath(), $"deckwraith-kernel-{Guid.NewGuid():N}");
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
