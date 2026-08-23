using System.Diagnostics;
using System.Text;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;

namespace Deckwraith.IntegrationTests;

public sealed class StateSpineEndToEndTests
{
    [Fact]
    public async Task AppliedRenameIsRecoveredWithExactlyOneArchiveEvent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var artifacts = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using var spine = new StateSpine(state, archive, artifacts, checkpoints, new FixedClock());
        await spine.InitializeAsync(CancellationToken.None);
        await spine.CreateWraithAsync("wraith1", CancellationToken.None);

        var intent = await state.RenameWraithAsync(
            CanonicalName.Parse("wraith1"),
            CanonicalName.Parse("vesper"),
            new FixedClock().UtcNow,
            CancellationToken.None);
        Assert.Equal(RenameStatus.Applied, intent.Status);

        Assert.Equal("vesper", (await spine.ResolveWraithAsync(
            "wraith1", CancellationToken.None)).Value);
        Assert.Equal("vesper", (await spine.ResolveWraithAsync(
            "wraith1", CancellationToken.None)).Value);
        var records = await spine.ReadArchiveAsync("wraith1", CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.Equal(intent.OperationId, records[^1].EventId);
        Assert.Equal("wraith.renamed", records[^1].Kind);
        Assert.Equal(string.Empty, await RunGitForTestsAsync(
            temporaryDirectory.Path,
            ["status", "--porcelain"],
            CancellationToken.None));
    }

    [Fact]
    public async Task StateSpinePreservesNamesHistoryArtifactsAndCleanCheckpoints()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var artifacts = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        using var spine = new StateSpine(state, archive, artifacts, checkpoints, new FixedClock());

        await spine.InitializeAsync(CancellationToken.None);
        await spine.CreateHauntAsync("Deckwraith", CancellationToken.None);
        var created = await spine.CreateWraithAsync("Wraith1", CancellationToken.None);
        Assert.Equal("wraith1", created.Value.Name);
        Assert.Empty(created.Value.Pronouns);
        Assert.Empty(created.Value.KnownTendencies);

        var artifactBytes = Encoding.UTF8.GetBytes("durable evidence");
        var artifact = await spine.StoreArtifactAsync(
            "wraith1",
            "deckwraith",
            new MemoryStream(artifactBytes),
            "text/plain",
            CancellationToken.None);
        await spine.AppendEventAsync(
            "wraith1",
            "milestone.observed",
            new { result = "state survives" },
            "deckwraith",
            CancellationToken.None);

        await spine.RenameWraithAsync(
            "wraith1", "Vesper", CancellationToken.None);
        await spine.RenameHauntAsync(
            "deckwraith", "Compiler-Lab", CancellationToken.None);

        Assert.Equal("vesper", (await spine.ResolveWraithAsync(
            "wraith1", CancellationToken.None)).Value);
        Assert.Equal("compiler-lab", (await spine.ResolveHauntAsync(
            "deckwraith", CancellationToken.None)).Value);
        Assert.Equal("vesper", (await spine.ReadIdentityAsync(
            "wraith1", CancellationToken.None)).Name);

        var records = await spine.ReadArchiveAsync(
            "wraith1", CancellationToken.None);
        Assert.Equal(4, records.Count);
        Assert.Equal([1L, 2L, 3L, 4L], records.Select(record => record.Sequence));
        Assert.Equal("wraith1", records[0].Agent);
        Assert.Equal("vesper", records[^1].Agent);
        Assert.Equal("wraith.renamed", records[^1].Kind);

        await using var stored = await artifacts.OpenReadAsync(
            CanonicalName.Parse("compiler-lab"),
            artifact.Value.Hash,
            CancellationToken.None);
        using var copied = new MemoryStream();
        await stored.CopyToAsync(copied, CancellationToken.None);
        Assert.Equal(artifactBytes, copied.ToArray());

        await Assert.ThrowsAsync<DeckStateException>(() => spine.CreateWraithAsync(
            "wraith1", CancellationToken.None));
        Assert.Equal(string.Empty, await RunGitForTestsAsync(
            temporaryDirectory.Path,
            ["status", "--porcelain"],
            CancellationToken.None));
        Assert.Equal(string.Empty, await RunGitForTestsAsync(
            temporaryDirectory.Path,
            ["remote"],
            CancellationToken.None));
        var commitCount = await RunGitForTestsAsync(
            temporaryDirectory.Path,
            ["rev-list", "--count", "HEAD"],
            CancellationToken.None);
        Assert.Equal("7", commitCount);
    }

    internal static async Task<string> RunGitForTestsAsync(
        string rootPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(rootPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class FixedClock : IDeckClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 23, 20, 15, 0, TimeSpan.Zero);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"deckwraith-integration-{Guid.NewGuid():N}");
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
