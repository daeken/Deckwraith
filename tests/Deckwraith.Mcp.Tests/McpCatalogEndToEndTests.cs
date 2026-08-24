using System.Diagnostics;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Mcp;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.Mcp.Tests;

public sealed class McpCatalogEndToEndTests
{
    [Fact]
    public async Task AssignmentsDiscoverSchemasWithoutExecutingAndExplicitCallsAreJournaled()
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
            await state.CreateWraithAsync("wraith2", CancellationToken.None);
        }

        var marker = Path.Combine(temporaryDirectory.Path, "mcp-side-effect.txt");
        var serverAssembly = Path.Combine(
            AppContext.BaseDirectory, "Deckwraith.Mcp.TestServer.dll");
        Assert.True(File.Exists(serverAssembly), serverAssembly);
        using var runtime = new McpCatalogRuntime(
            temporaryDirectory.Path, deckState, archive, checkpoints, clock);
        await runtime.ConfigureServersAsync(
        [
            new McpServerDefinition(
                "fake",
                "dotnet",
                [serverAssembly, marker],
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                10),
        ]);
        await runtime.WriteGlobalAssignmentAsync(new McpAssignmentDocument(
            McpAssignmentDocument.CurrentSchemaVersion,
            ["fake"],
            [],
            [],
            [],
            clock.UtcNow));
        await runtime.WriteWraithAssignmentAsync("wraith1", new McpAssignmentDocument(
            McpAssignmentDocument.CurrentSchemaVersion,
            [],
            [],
            [],
            ["fake/hidden_probe"],
            clock.UtcNow));
        await runtime.WriteWraithAssignmentAsync("wraith2", new McpAssignmentDocument(
            McpAssignmentDocument.CurrentSchemaVersion,
            [],
            [],
            ["fake"],
            [],
            clock.UtcNow));

        var catalog = await runtime.GetEffectiveCatalogAsync("wraith1");
        var tool = Assert.Single(catalog.Tools);
        Assert.Equal("fake/emit_structured_side_effect", tool.QualifiedName);
        Assert.Equal("Invoke-DwFakeEmitStructuredSideEffect", tool.PowerShellCommand);
        Assert.Equal("Deckwraith.Mcp.Fake", tool.PowerShellModule);
        Assert.Equal(
            "integer",
            tool.InputSchema.GetProperty("properties").GetProperty("count")
                .GetProperty("type").GetString());
        Assert.Contains(
            "label",
            tool.InputSchema.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()));
        Assert.False(File.Exists(marker));
        Assert.Empty((await runtime.GetEffectiveCatalogAsync("wraith2")).Tools);

        var result = await runtime.CallToolAsync(
            tool.QualifiedName,
            JsonSerializer.SerializeToElement(new { label = "explicit", count = 3 }),
            new McpInvocationContext(
                "wraith1", null, "run-1", "shell-1", "mcp-operation-1"));

        Assert.False(result.IsError);
        Assert.Equal("explicit", result.StructuredContent.GetProperty("label").GetString());
        Assert.Equal(3, result.StructuredContent.GetProperty("count").GetInt32());
        Assert.True(result.StructuredContent.GetProperty("nested")
            .GetProperty("preserved").GetBoolean());
        Assert.Equal([1, 2, 3], result.StructuredContent.GetProperty("nested")
            .GetProperty("values").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(["explicit:3"], await File.ReadAllLinesAsync(marker));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        var started = Assert.Single(records, record => record.Kind == "mcp.started");
        var completed = Assert.Single(records, record => record.Kind == "mcp.completed");
        Assert.Equal("mcp-operation-1", started.EventId);
        Assert.Equal(
            "mcp-operation-1",
            completed.Payload.GetProperty("operationId").GetString());
        Assert.Equal(
            started.Sequence,
            completed.Payload.GetProperty("startedSequence").GetInt64());
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
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(error), error);
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
                System.IO.Path.GetTempPath(), $"deckwraith-mcp-{Guid.NewGuid():N}");
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
