using System.Diagnostics;
using System.Management.Automation;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;

namespace Deckwraith.PowerShell.Tests;

public sealed class RunspaceLossTests
{
    private const string RunId = "00000000000000000000000000000001";

    [Fact]
    public async Task ColdReplacementDropsVariablesPreservesStateReloadsToolsAndNeverReplays()
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

        await inferenceState.CreateRunAsync(
            CanonicalName.Parse("wraith1"),
            new RunDocument(
                RunDocument.CurrentSchemaVersion,
                RunId,
                "wraith1",
                "deckwraith",
                "Prove cold runspace replacement",
                RunStatus.Created,
                null,
                [new ShellDocument(
                    "shell-1", "fake", "test-model", clock.UtcNow, null, null)],
                clock.UtcNow,
                clock.UtcNow),
            CancellationToken.None);
        await checkpoints.CheckpointAsync(
            "test-run-created",
            CanonicalName.Parse("wraith1"),
            CanonicalName.Parse("deckwraith"),
            CancellationToken.None);

        var toolPath = Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "tools", "wraith-echo.ps1");
        await File.WriteAllTextAsync(toolPath, ToolSource(1));
        var markerPath = Path.Combine(temporaryDirectory.Path, "pipeline-marker.txt");
        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints,
            clock);
        using var manager = new PowerShellRuntimeManager(
            temporaryDirectory.Path,
            durableState,
            archive,
            checkpoints,
            clock);
        var invocation = new PowerShellInvocationContext("wraith1", RunId, "deckwraith");

        var first = await manager.ExecuteAsync(invocation, $$"""
            $volatile = 41
            Add-Content -LiteralPath {{Quote(markerPath)}} -Value 'executed'
            $saved = Set-DwState -Name 'kept' -Scope Agent -ExpectedVersion 0 -Value ([ordered]@{ count = 7 })
            [pscustomobject]@{
                StateVersion = $saved.Version
                ToolGeneration = (Invoke-WraithEcho -Text 'before').Generation
                CommandType = (Get-Command Set-DwState).CommandType.ToString()
                LanguageMode = $ExecutionContext.SessionState.LanguageMode.ToString()
            }
            """);
        Assert.Empty(first.Errors);
        var firstSummary = first.Output[^1];
        Assert.Equal(1L, Property<long>(firstSummary, "StateVersion"));
        Assert.Equal(1, Property<int>(firstSummary, "ToolGeneration"));
        Assert.Equal("Cmdlet", Property<string>(firstSummary, "CommandType"));
        Assert.Equal("FullLanguage", Property<string>(firstSummary, "LanguageMode"));
        Assert.Equal(1, first.Runtime.Epoch);

        var replacement = await manager.ReplaceAsync(
            invocation, "acceptance-test-loss", CancellationToken.None);
        Assert.Equal(2, replacement.Epoch);
        Assert.True(replacement.VolatileStateLost);

        var afterLoss = await manager.ExecuteAsync(invocation, """
            $saved = Get-DwState -Name 'kept' -Scope Agent
            [pscustomobject]@{
                VolatileExists = [bool](Test-Path variable:volatile)
                DurableCount = [int]$saved.count
                ToolGeneration = (Invoke-WraithEcho -Text 'after').Generation
                Epoch = (Get-DwRuntime).Epoch
                ToolCount = @(Get-DwTool).Count
            }
            """);
        var lossSummary = Assert.Single(afterLoss.Output);
        Assert.False(Property<bool>(lossSummary, "VolatileExists"));
        Assert.Equal(7, Property<int>(lossSummary, "DurableCount"));
        Assert.Equal(1, Property<int>(lossSummary, "ToolGeneration"));
        Assert.Equal(2L, Property<long>(lossSummary, "Epoch"));
        Assert.Equal(1, Property<int>(lossSummary, "ToolCount"));
        Assert.Single(await File.ReadAllLinesAsync(markerPath));

        await File.WriteAllTextAsync(toolPath, ToolSource(2));
        var reload = await manager.ExecuteAsync(
            invocation, "Reload-DwTools", CancellationToken.None);
        Assert.True(reload.ToolsReloaded);
        Assert.Equal(3, reload.Runtime.Epoch);
        var reloaded = await manager.ExecuteAsync(
            invocation,
            "(Invoke-WraithEcho -Text 'reloaded').Generation",
            CancellationToken.None);
        Assert.Equal(2, Assert.IsType<int>(Assert.Single(reloaded.Output).BaseObject));

        await File.WriteAllTextAsync(toolPath, "function Invoke-WraithEcho {");
        await Assert.ThrowsAsync<PowerShellToolLoadException>(() => manager.ExecuteAsync(
            invocation, "Reload-DwTools", CancellationToken.None));
        var retained = await manager.ExecuteAsync(
            invocation,
            "(Invoke-WraithEcho -Text 'retained').Generation",
            CancellationToken.None);
        Assert.Equal(2, Assert.IsType<int>(Assert.Single(retained.Output).BaseObject));
        Assert.Single(await File.ReadAllLinesAsync(markerPath));

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal(2, records.Count(record => record.Kind == "runspace.replaced"));
        Assert.Single(records, record => record.Kind == "runspace.reload-failed");
        Assert.All(
            records.Where(record => record.Kind == "runspace.replaced"),
            record => Assert.False(record.Payload.GetProperty("replayedCommands").GetBoolean()));
        Assert.Equal(string.Empty, await RunGitAsync(
            temporaryDirectory.Path, ["status", "--porcelain"]));
    }

    private static string ToolSource(int generation) => $$"""
        function Invoke-WraithEcho {
            [CmdletBinding()]
            param([Parameter(Mandatory)][string] $Text)

            [pscustomobject]@{
                Text = $Text
                Generation = {{generation}}
            }
        }
        """;

    private static T Property<T>(PSObject value, string name) =>
        Assert.IsType<T>(value.Properties[name].Value);

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
                System.IO.Path.GetTempPath(), $"deckwraith-powershell-{Guid.NewGuid():N}");
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
