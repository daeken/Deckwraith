using System.Management.Automation;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deckwraith.Application.Files;
using Deckwraith.Application.State;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.PowerShell.Serialization;

namespace Deckwraith.PowerShell.Tests;

public sealed class AtomicFileEditorTests
{
    [Fact]
    public async Task HostedEditUsesTheHauntProjectAndCreatesOneAttributedCommit()
    {
        using var deckDirectory = new TemporaryDirectory();
        using var projectDirectory = new TemporaryDirectory();
        await InitializeGitProjectAsync(projectDirectory.Path);
        var notePath = Path.Combine(projectDirectory.Path, "note.txt");
        var guardedPath = Path.Combine(projectDirectory.Path, "guarded.txt");
        await File.WriteAllTextAsync(notePath, "hello\n");
        await File.WriteAllTextAsync(guardedPath, "untouched\n");
        await GitAsync(projectDirectory.Path, ["add", "--all"]);
        await GitAsync(projectDirectory.Path, ["commit", "-m", "baseline"]);

        var deckState = new JsonDeckStateStore(deckDirectory.Path);
        var archive = new JsonlAgentArchive(deckDirectory.Path);
        var checkpoints = new GitCheckpointStore(deckDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(deckDirectory.Path);
        using (var state = new StateSpine(deckState, archive, artifactStore, checkpoints))
        {
            await state.InitializeAsync();
            await state.CreateHauntAsync("work");
            await state.CreateWraithAsync("lumen");
            await state.ConfigureHauntProjectAsync(
                "work",
                projectDirectory.Path,
                autoCommitEnabled: true,
                cancellationToken: CancellationToken.None);
        }

        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(deckDirectory.Path),
            archive,
            checkpoints);
        var artifacts = new ArtifactRuntime(deckState, artifactStore, archive, checkpoints);
        using var manager = new PowerShellRuntimeManager(
            deckDirectory.Path,
            durableState,
            artifacts,
            archive,
            checkpoints,
            deckState: deckState,
            projectCommitter: new GitProjectCommitter());

        var execution = await manager.ExecuteAsync(
            new PowerShellInvocationContext("lumen", Haunt: "work"),
            """
            Invoke-DwFileEdit -Operation @(
                @{ path = 'note.txt'; kind = 'append'; text = 'from lumen' }
            ) -CommitSubject 'Continue the note' -CommitBody 'Use the haunt project by default.'
            """);

        Assert.Empty(execution.Errors);
        var portable = PortablePowerShellValue.ToJsonElement(Assert.Single(execution.Output));
        Assert.Equal(1, portable.GetProperty("files").GetArrayLength());
        var commit = portable.GetProperty("commit");
        Assert.NotEmpty(commit.GetProperty("commitId").GetString() ?? string.Empty);
        Assert.Equal(1, commit.GetProperty("paths").GetArrayLength());
        Assert.Equal("lumen@deckwraith.local", commit.GetProperty("authorEmail").GetString());
        Assert.Equal("hello\nfrom lumen", await File.ReadAllTextAsync(notePath));
        Assert.Equal("2", await GitAsync(
            projectDirectory.Path, ["rev-list", "--count", "HEAD"]));
        Assert.Equal("note.txt", await GitAsync(
            projectDirectory.Path,
            ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"]));

        var rejected = await manager.ExecuteAsync(
            new PowerShellInvocationContext("lumen", Haunt: "work"),
            """
            Invoke-DwFileEdit -Operation @(
                [pscustomobject]@{ path = 'guarded.txt'; kind = 'append'; text = 'should not land' }
            )
            """);
        Assert.NotEmpty(rejected.Errors);
        Assert.Equal("untouched\n", await File.ReadAllTextAsync(guardedPath));
        Assert.Equal("2", await GitAsync(
            projectDirectory.Path, ["rev-list", "--count", "HEAD"]));
    }

    [Fact]
    public async Task HostedAutoCommitFailureRollsBackEveryPublishedFile()
    {
        using var deckDirectory = new TemporaryDirectory();
        using var projectDirectory = new TemporaryDirectory();
        var existingPath = Path.Combine(projectDirectory.Path, "existing.txt");
        var createdPath = Path.Combine(projectDirectory.Path, "created.txt");
        await File.WriteAllTextAsync(existingPath, "original\n");

        var deckState = new JsonDeckStateStore(deckDirectory.Path);
        var archive = new JsonlAgentArchive(deckDirectory.Path);
        var checkpoints = new GitCheckpointStore(deckDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(deckDirectory.Path);
        using (var state = new StateSpine(deckState, archive, artifactStore, checkpoints))
        {
            await state.InitializeAsync();
            await state.CreateHauntAsync("work");
            await state.CreateWraithAsync("lumen");
            await state.ConfigureHauntProjectAsync(
                "work",
                projectDirectory.Path,
                autoCommitEnabled: true,
                cancellationToken: CancellationToken.None);
        }

        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(deckDirectory.Path),
            archive,
            checkpoints);
        var artifacts = new ArtifactRuntime(deckState, artifactStore, archive, checkpoints);
        var committer = new FailingProjectCommitter();
        using var manager = new PowerShellRuntimeManager(
            deckDirectory.Path,
            durableState,
            artifacts,
            archive,
            checkpoints,
            deckState: deckState,
            projectCommitter: committer);

        var execution = await manager.ExecuteAsync(
            new PowerShellInvocationContext("lumen", Haunt: "work"),
            """
            Invoke-DwFileEdit -Operation @(
                @{ path = 'existing.txt'; kind = 'append'; text = 'published' },
                @{ path = 'created.txt'; kind = 'write'; text = 'published' }
            ) -CommitSubject 'This commit will fail'
            """);

        Assert.NotEmpty(execution.Errors);
        Assert.Equal(1, committer.CommitCallCount);
        Assert.Equal("original\n", await File.ReadAllTextAsync(existingPath));
        Assert.False(File.Exists(createdPath));
        Assert.Empty(Directory.EnumerateFiles(
            projectDirectory.Path, ".deckwraith-edit-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TextAndStructuralJsonOperationsPublishAsOneValidatedBatch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var textPath = Path.Combine(temporaryDirectory.Path, "note.txt");
        var jsonPath = Path.Combine(temporaryDirectory.Path, "config.json");
        await File.WriteAllTextAsync(textPath, "body\n");
        await File.WriteAllTextAsync(jsonPath, """
            {
              "name": "old",
              "items": [1],
              "obsolete": true
            }
            """);
        var expectedTextHash = Hash(await File.ReadAllBytesAsync(textPath));

        var result = await AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
        [
            new("note.txt", FileEditKind.Prepend, Text: "header\n", ExpectedHash: expectedTextHash),
            new("note.txt", FileEditKind.Replace, Match: "body", Replacement: "core"),
            new("note.txt", FileEditKind.Append, Text: "footer\n"),
            new("config.json", FileEditKind.JsonTest, JsonPointer: "/name", Value: Json("old")),
            new("config.json", FileEditKind.JsonSet, JsonPointer: "/name", Value: Json("new")),
            new("config.json", FileEditKind.JsonInsert, JsonPointer: "/items", JsonIndex: 0, Value: Json(0)),
            new("config.json", FileEditKind.JsonAppend, JsonPointer: "/items", Value: Json(2)),
            new("config.json", FileEditKind.JsonRemove, JsonPointer: "/obsolete"),
        ], temporaryDirectory.Path, "Tune the fixture", "Apply text and JSON edits together."));

        Assert.Equal("header\ncore\nfooter\n", await File.ReadAllTextAsync(textPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal("new", document.RootElement.GetProperty("name").GetString());
        Assert.Equal([0, 1, 2], document.RootElement.GetProperty("items")
            .EnumerateArray().Select(item => item.GetInt32()));
        Assert.False(document.RootElement.TryGetProperty("obsolete", out _));
        Assert.Equal("Tune the fixture", result.CommitSubject);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Files, file => file.BeforeHash == expectedTextHash);
        Assert.All(result.Files, file => Assert.StartsWith("sha256:", file.AfterHash));
        Assert.Empty(Directory.EnumerateFiles(
            temporaryDirectory.Path, ".deckwraith-edit-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NativeEquivalentPathCasingIsOneOrderedFileBatch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var actualPath = Path.Combine(temporaryDirectory.Path, "MixedCase.txt");
        var alternatePath = Path.Combine(temporaryDirectory.Path, "mixedcase.txt");
        await File.WriteAllTextAsync(actualPath, "body");
        if (!File.Exists(alternatePath))
        {
            return;
        }

        var result = await AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
        [
            new("MixedCase.txt", FileEditKind.Prepend, Text: "before-"),
            new("mixedcase.txt", FileEditKind.Append, Text: "-after"),
        ], temporaryDirectory.Path));

        Assert.Equal("before-body-after", await File.ReadAllTextAsync(actualPath));
        var receipt = Assert.Single(result.Files);
        Assert.Equal(
            [FileEditKind.Prepend, FileEditKind.Append],
            receipt.Operations);
    }

    [Fact]
    public async Task CaseSensitiveVolumesKeepDifferentlyCasedPathsDistinct()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var upperPath = Path.Combine(temporaryDirectory.Path, "Twin.txt");
        var lowerPath = Path.Combine(temporaryDirectory.Path, "twin.txt");
        await File.WriteAllTextAsync(upperPath, "upper");
        if (File.Exists(lowerPath))
        {
            return;
        }

        await File.WriteAllTextAsync(lowerPath, "lower");
        var result = await AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
        [
            new("Twin.txt", FileEditKind.Append, Text: "-edited"),
            new("twin.txt", FileEditKind.Append, Text: "-edited"),
        ], temporaryDirectory.Path));

        Assert.Equal("upper-edited", await File.ReadAllTextAsync(upperPath));
        Assert.Equal("lower-edited", await File.ReadAllTextAsync(lowerPath));
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public async Task MissingAnchorOrEscapingRootLeavesEveryFileUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = Path.Combine(temporaryDirectory.Path, "left.txt");
        var right = Path.Combine(temporaryDirectory.Path, "right.txt");
        await File.WriteAllTextAsync(left, "left");
        await File.WriteAllTextAsync(right, "right");

        var anchorError = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
            AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
            [
                new("left.txt", FileEditKind.Write, Text: "changed"),
                new("right.txt", FileEditKind.Replace, Match: "missing", Replacement: "changed"),
            ], temporaryDirectory.Path)));

        Assert.Contains("expected 1 occurrence", anchorError.Message, StringComparison.Ordinal);
        Assert.Equal("left", await File.ReadAllTextAsync(left));
        Assert.Equal("right", await File.ReadAllTextAsync(right));
        await Assert.ThrowsAsync<AtomicFileEditException>(() => AtomicFileEditor.ApplyAsync(
            new AtomicFileEditBatch(
                [new("../outside.txt", FileEditKind.Write, Text: "nope")],
                temporaryDirectory.Path)));
        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "..", "outside.txt")));
    }

    [Fact]
    public async Task SymbolicLinksCannotEscapeTheEditRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using var outsideDirectory = new TemporaryDirectory();
        var outsidePath = Path.Combine(outsideDirectory.Path, "outside.txt");
        await File.WriteAllTextAsync(outsidePath, "untouched");
        Directory.CreateSymbolicLink(
            Path.Combine(temporaryDirectory.Path, "linked"),
            outsideDirectory.Path);

        var error = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
            AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
                [new("linked/outside.txt", FileEditKind.Write, Text: "escaped")],
                temporaryDirectory.Path)));

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal("untouched", await File.ReadAllTextAsync(outsidePath));
    }

    [Fact]
    public async Task StaleHashesAndAmbiguousAnchorsRejectTheWholeBatch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var guardedPath = Path.Combine(temporaryDirectory.Path, "guarded.txt");
        var neighborPath = Path.Combine(temporaryDirectory.Path, "neighbor.txt");
        await File.WriteAllTextAsync(guardedPath, "same same");
        await File.WriteAllTextAsync(neighborPath, "neighbor");

        var stale = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
            AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
            [
                new("guarded.txt", FileEditKind.Write, Text: "changed", ExpectedHash: "sha256:stale"),
                new("neighbor.txt", FileEditKind.Write, Text: "also changed"),
            ], temporaryDirectory.Path)));
        Assert.Contains("Expected hash", stale.Message, StringComparison.Ordinal);
        Assert.Equal("same same", await File.ReadAllTextAsync(guardedPath));
        Assert.Equal("neighbor", await File.ReadAllTextAsync(neighborPath));

        var ambiguous = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
            AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
            [
                new("guarded.txt", FileEditKind.Replace, Match: "same", Replacement: "different"),
                new("neighbor.txt", FileEditKind.Write, Text: "also changed"),
            ], temporaryDirectory.Path)));
        Assert.Contains("found 2", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal("same same", await File.ReadAllTextAsync(guardedPath));
        Assert.Equal("neighbor", await File.ReadAllTextAsync(neighborPath));
    }

    [Fact]
    public async Task LaterPublicationFailureRestoresEarlierFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var publishedFirstPath = Path.Combine(temporaryDirectory.Path, "a-first.txt");
        var blockedPath = Path.Combine(temporaryDirectory.Path, "z-blocked");
        Directory.CreateDirectory(blockedPath);

        var error = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
            AtomicFileEditor.ApplyAsync(new AtomicFileEditBatch(
            [
                new("a-first.txt", FileEditKind.Write, Text: "must roll back"),
                new("z-blocked", FileEditKind.Write, Text: "cannot replace a directory"),
            ], temporaryDirectory.Path)));

        Assert.Contains("all published files were restored", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(publishedFirstPath));
        Assert.True(Directory.Exists(blockedPath));
        Assert.Empty(Directory.EnumerateFiles(
            temporaryDirectory.Path, ".deckwraith-edit-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HostedPowerShellExposesStructuredAtomicEditing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var deckState = new JsonDeckStateStore(temporaryDirectory.Path);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);
        var artifactStore = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        using (var state = new StateSpine(deckState, archive, artifactStore, checkpoints))
        {
            await state.InitializeAsync();
            await state.CreateHauntAsync("work");
            await state.CreateWraithAsync("steward");
        }

        var workspace = Path.Combine(temporaryDirectory.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "note.txt"), "world");
        await File.WriteAllTextAsync(Path.Combine(workspace, "settings.json"), "{\"enabled\":false}");
        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(temporaryDirectory.Path),
            archive,
            checkpoints);
        var artifacts = new ArtifactRuntime(deckState, artifactStore, archive, checkpoints);
        using var manager = new PowerShellRuntimeManager(
            temporaryDirectory.Path, durableState, artifacts, archive, checkpoints);

        var execution = await manager.ExecuteAsync(
            new PowerShellInvocationContext("steward", Haunt: "work"),
            $$"""
            $ops = @(
                [pscustomobject]@{ path = 'note.txt'; kind = 'prepend'; text = 'hello ' },
                [pscustomobject]@{ path = 'settings.json'; kind = 'json-set'; pointer = '/enabled'; value = $true }
            )
            $result = Invoke-DwFileEdit -RootPath {{Quote(workspace)}} -Operation $ops -CommitSubject 'Personalize workspace'
            [pscustomobject]@{
                CommandType = (Get-Command Invoke-DwFileEdit).CommandType.ToString()
                FileCount = $result.Files.Count
                CommitSubject = $result.CommitSubject
            }
            """);

        Assert.Empty(execution.Errors);
        var summary = Assert.Single(execution.Output);
        Assert.Equal("Cmdlet", Property<string>(summary, "CommandType"));
        Assert.Equal(2, Property<int>(summary, "FileCount"));
        Assert.Equal("Personalize workspace", Property<string>(summary, "CommitSubject"));
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(workspace, "note.txt")));
        using var settings = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(workspace, "settings.json")));
        Assert.True(settings.RootElement.GetProperty("enabled").GetBoolean());
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static string Hash(byte[] value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static T Property<T>(PSObject value, string name) =>
        Assert.IsType<T>(value.Properties[name].Value);

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task InitializeGitProjectAsync(string path)
    {
        await GitAsync(path, ["init", "--initial-branch=main"]);
        await GitAsync(path, ["config", "user.name", "Test Human"]);
        await GitAsync(path, ["config", "user.email", "human@example.test"]);
    }

    private static async Task<string> GitAsync(
        string path,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(path);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Git.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"deckwraith-file-edit-{Guid.NewGuid():N}");
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

    private sealed class FailingProjectCommitter : IProjectCommitter
    {
        public int CommitCallCount { get; private set; }

        public Task<ProjectCommitPreparation> PrepareAsync(
            HauntProjectPolicy policy,
            CanonicalName wraith,
            CanonicalName haunt,
            string subject,
            string? body,
            IReadOnlyList<string> targetPaths,
            CancellationToken cancellationToken) => Task.FromResult(new ProjectCommitPreparation(
                policy.ProjectPath,
                policy.ProjectPath,
                wraith,
                haunt,
                subject,
                body,
                wraith.Value,
                $"{wraith.Value}@deckwraith.local",
                targetPaths,
                targetPaths.Select(path => Path.GetRelativePath(policy.ProjectPath, path)).ToArray()));

        public Task<ProjectCommitReceipt?> CommitAsync(
            ProjectCommitPreparation preparation,
            IReadOnlyList<FileEditReceipt> files,
            CancellationToken cancellationToken)
        {
            CommitCallCount++;
            return Task.FromException<ProjectCommitReceipt?>(
                new ProjectCommitException("Deliberate auto-commit failure."));
        }
    }
}
