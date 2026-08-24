using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Mcp;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Tests;

public sealed class DeckbookToolAcceptanceTests
{
    [Fact]
    public async Task ExplicitSuffixUsesAuthoredAndMcpToolsWithDurableProvenance()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clock = new FixedClock();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        using (var state = new StateSpine(
            deckState, archive, artifactStore, checkpoints, clock))
        {
            await state.InitializeAsync(CancellationToken.None);
            await state.CreateHauntAsync("deckwraith", CancellationToken.None);
            await state.CreateWraithAsync("wraith1", CancellationToken.None);
        }

        var toolPath = Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "tools", "wraith-echo.ps1");
        await File.WriteAllTextAsync(toolPath, """
            function Invoke-WraithEcho {
                [CmdletBinding()]
                param([Parameter(Mandatory)][string] $Text)

                [pscustomobject]@{ text = $Text; generation = 1 }
            }
            """);

        var marker = Path.Combine(temporaryDirectory.Path, "mcp-cell-side-effect.txt");
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

        var durable = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        var artifacts = new ArtifactRuntime(
            deckState, artifactStore, archive, checkpoints, clock);
        using var runspaces = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            durable,
            artifacts,
            archive,
            checkpoints,
            clock,
            mcp);
        using var kernel = new PowerShellCellKernel(runspaces);
        using var notebooks = new DeckbookRuntime(
            temporaryDirectory.Path,
            deckState,
            new CellKernelRegistry([kernel]),
            archive,
            checkpoints,
            clock);

        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "authored-tool",
                DeckbookCellKind.Code,
                "Invoke-WraithEcho -Text 'from-cell'",
                "powershell"));
        await notebooks.InsertAsync(
            "wraith1",
            "deckwraith",
            new InsertDeckbookCell(
                "mcp-side-effect",
                DeckbookCellKind.Code,
                "Invoke-DwFakeEmitStructuredSideEffect -Label 'from-cell' -Count 2",
                "powershell"));

        Assert.False(File.Exists(marker));
        var execution = await notebooks.RunRemainingAsync(
            "wraith1", "deckwraith", "authored-tool");

        Assert.True(execution.Completed);
        Assert.Equal(2, execution.Executions.Count);
        Assert.Equal(
            "from-cell",
            execution.Executions[0].Output.Values[^1].GetProperty("text").GetString());
        Assert.Equal(
            1,
            execution.Executions[0].Output.Values[^1].GetProperty("generation").GetInt32());
        Assert.True(execution.Executions[1].Output.Values[^1]
            .GetProperty("nested").GetProperty("preserved").GetBoolean());
        Assert.Equal(["from-cell:2"], await File.ReadAllLinesAsync(marker));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(2, records.Count(record =>
            record.Kind == "deckbook.cell-execution-started"));
        Assert.Equal(2, records.Count(record =>
            record.Kind == "deckbook.cell-execution-completed"));
        Assert.Single(records, record => record.Kind == "mcp.started");
        Assert.Single(records, record => record.Kind == "mcp.completed");
        var committedTool = await RunspaceLossTests.RunGitForTestsAsync(
            temporaryDirectory.Path,
            ["show", "HEAD:agents/wraith1/tools/wraith-echo.ps1"]);
        Assert.Contains("Invoke-WraithEcho", committedTool);
        Assert.Equal(string.Empty, await RunspaceLossTests.RunGitForTestsAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
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
                System.IO.Path.GetTempPath(), $"deckwraith-cell-tools-{Guid.NewGuid():N}");
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
