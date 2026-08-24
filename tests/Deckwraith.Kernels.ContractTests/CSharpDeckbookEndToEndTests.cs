using System.Diagnostics;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Serialization;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.CSharp;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.Kernels.ContractTests;

public sealed class CSharpDeckbookEndToEndTests
{
    [Fact]
    public async Task CSharpCellsKeepAmbientStateInterruptAndReplaceColdWithoutReplay()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifacts = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var clock = new FixedClock();
        using (var state = new StateSpine(
            deckState,
            archive,
            artifacts,
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
            deckState, artifacts, archive, checkpoints, clock);
        using var kernel = new CSharpCellKernel(
            durableState, artifactRuntime, archive, checkpoints, clock);
        using var notebooks = new DeckbookRuntime(
            temporaryDirectory.Path,
            deckState,
            new CellKernelRegistry([kernel]),
            archive,
            checkpoints,
            clock);
        var markerPath = Path.Combine(temporaryDirectory.Path, "csharp-marker.txt");

        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "declare",
                DeckbookCellKind.Code,
                $"File.AppendAllText({Quote(markerPath)}, \"ran\\n\"); var counter = 40; counter",
                "csharp"));
        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "increment",
                DeckbookCellKind.Code,
                "counter += DwCellInput.GetProperty(\"increment\").GetInt32(); new { counter }",
                "csharp"));

        var initial = await notebooks.RunRemainingAsync(
            "wraith1",
            "deckwraith",
            "declare",
            input: CanonicalJson.ToElement(new { increment = 2 }));
        Assert.True(initial.Completed);
        Assert.Equal(2, initial.Executions.Count);
        Assert.Equal(40, initial.Executions[0].Output.Values[^1].GetInt32());
        Assert.Equal(
            42,
            initial.Executions[1].Output.Values[^1].GetProperty("counter").GetInt32());
        Assert.Equal(1, initial.Executions[1].Cell.Cell.LastExecution?.KernelEpoch);
        Assert.Equal("csharp", initial.Executions[1].Cell.Cell.LastExecution?.Kernel);
        Assert.NotEqual("unknown", initial.Executions[1].Cell.Cell.LastExecution?.KernelVersion);
        Assert.True(File.Exists(Path.Combine(
            temporaryDirectory.Path,
            "agents",
            "wraith1",
            "deckbooks",
            "deckwraith",
            "cells",
            "declare",
            "source.csx")));

        await using (var events = kernel.ExecuteAsync(
            new CellExecutionRequest(
                "interrupt-csharp",
                "wraith1",
                RunId: null,
                "deckwraith",
                "interrupt-csharp",
                "await Task.Delay(Timeout.InfiniteTimeSpan, Dw.Cancellation); 1",
                CanonicalJson.ToElement<object?>(null)),
            CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync());
            Assert.IsType<CellKernelStarted>(events.Current);
            var pending = events.MoveNextAsync().AsTask();
            await kernel.InterruptAsync("interrupt-csharp", CancellationToken.None);
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var completed = Assert.IsType<CellKernelCompleted>(events.Current);
            Assert.Equal(CellKernelExecutionStatus.Cancelled, completed.Status);
            Assert.False(await events.MoveNextAsync());
        }

        var beforeReplacement = await notebooks.GetAsync("wraith1", "deckwraith");
        var retainedOutput = Assert.Single(
            beforeReplacement.Cells,
            cell => cell.Cell.Name == "increment").Cell.LastExecution?.OutputHash;
        Assert.NotNull(retainedOutput);
        var replacement = await kernel.ReplaceAsync(
            "wraith1",
            runId: null,
            "deckwraith",
            "kernel-loss-test",
            CancellationToken.None);
        Assert.Equal(2, replacement.Epoch);
        Assert.True(replacement.VolatileStateLost);

        var cold = await notebooks.RunCellAsync(
            "wraith1",
            "deckwraith",
            "increment",
            input: CanonicalJson.ToElement(new { increment = 1 }));
        Assert.Equal(CellKernelExecutionStatus.Failed, cold.Output.Status);
        Assert.Contains(
            cold.Output.Errors,
            error => error.Contains("counter", StringComparison.Ordinal));
        Assert.Equal(2, cold.Cell.Cell.LastExecution?.KernelEpoch);
        Assert.Single(await File.ReadAllLinesAsync(markerPath));
        Assert.True(File.Exists(Path.Combine(
            temporaryDirectory.Path,
            "agents",
            "wraith1",
            "deckbooks",
            "deckwraith",
            "outputs",
            retainedOutput[7..] + ".json")));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    [Fact]
    public async Task PowerShellAndCSharpExchangeCanonicalValuesAndArtifacts()
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
        using var powerShell = new PowerShellCellKernel(runspaces);
        using var csharp = new CSharpCellKernel(
            durableState, artifactRuntime, archive, checkpoints, clock);
        using var notebooks = new DeckbookRuntime(
            temporaryDirectory.Path,
            deckState,
            new CellKernelRegistry([powerShell, csharp]),
            archive,
            checkpoints,
            clock);

        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "publish-value",
                DeckbookCellKind.Code,
                "await Dw.SetStateAsync(\"cross-value\", new { answer = 42 }, " +
                "expectedVersion: 0); \"published\"",
                "csharp"));
        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "publish-artifact",
                DeckbookCellKind.Code,
                "$value = Get-DwState -Name 'cross-value' -Scope Agent\n" +
                "$artifact = Set-DwArtifact -Content 'from powershell' " +
                "-MediaType 'text/plain; charset=utf-8'\n" +
                "Set-DwState -Name 'artifact-hash' -Value $artifact.Hash " +
                "-Scope Agent -ExpectedVersion 0 | Out-Null\n" +
                "[pscustomobject]@{ answer = [int]$value.answer; artifact = $artifact.Hash }",
                "powershell"));
        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "consume-artifact",
                DeckbookCellKind.Code,
                "var stored = await Dw.GetStateAsync(\"artifact-hash\"); " +
                "var hash = stored!.Value.GetString()!; " +
                "new { hash, text = await Dw.ReadArtifactTextAsync(hash) }",
                "csharp"));

        var result = await notebooks.RunRemainingAsync(
            "wraith1", "deckwraith", "publish-value");

        Assert.True(result.Completed);
        Assert.Equal(3, result.Executions.Count);
        var powerShellValue = result.Executions[1].Output.Values[^1];
        Assert.Equal(42, powerShellValue.GetProperty("answer").GetInt32());
        var artifactHash = powerShellValue.GetProperty("artifact").GetString();
        Assert.StartsWith("sha256:", artifactHash, StringComparison.Ordinal);
        var csharpValue = result.Executions[2].Output.Values[^1];
        Assert.Equal(artifactHash, csharpValue.GetProperty("hash").GetString());
        Assert.Equal("from powershell", csharpValue.GetProperty("text").GetString());
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private static string Quote(string value) =>
        "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
                System.IO.Path.GetTempPath(), $"deckwraith-csharp-{Guid.NewGuid():N}");
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
