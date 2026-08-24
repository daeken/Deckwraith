using System.Diagnostics;
using Deckwraith.Application.Files;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Git;

namespace Deckwraith.Persistence.Tests;

public sealed class GitProjectCommitterTests
{
    [Fact]
    public async Task CommitsOnlyEditedFilesAndPreservesTheExistingIndexAndWorktree()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await InitializeProjectAsync(temporaryDirectory.Path);
        var editedPath = Path.Combine(temporaryDirectory.Path, "edited.txt");
        var stagedPath = Path.Combine(temporaryDirectory.Path, "staged.txt");
        var unstagedPath = Path.Combine(temporaryDirectory.Path, "unstaged.txt");
        await File.WriteAllTextAsync(editedPath, "original\n");
        await File.WriteAllTextAsync(stagedPath, "original\n");
        await File.WriteAllTextAsync(unstagedPath, "original\n");
        await GitAsync(temporaryDirectory.Path, ["add", "--all"]);
        await GitAsync(temporaryDirectory.Path, ["commit", "-m", "baseline"]);
        await File.WriteAllTextAsync(stagedPath, "human staged\n");
        await GitAsync(temporaryDirectory.Path, ["add", "--", "staged.txt"]);
        await File.WriteAllTextAsync(unstagedPath, "human unstaged\n");

        var batch = new AtomicFileEditBatch(
            [new("edited.txt", FileEditKind.Append, Text: "wraith edit\n")],
            temporaryDirectory.Path,
            "Adapt the project",
            "Keep the neighboring human work intact.");
        var committer = new GitProjectCommitter();
        var preparation = await committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: true),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            batch.CommitSubject!,
            batch.CommitBody,
            AtomicFileEditor.ResolvePaths(batch),
            CancellationToken.None);
        var edit = await AtomicFileEditor.ApplyAsync(batch);

        var commit = Assert.IsType<ProjectCommitReceipt>(await committer.CommitAsync(
            preparation, edit.Files, CancellationToken.None));

        Assert.Equal(["edited.txt"], commit.Paths);
        Assert.Equal("original\nwraith edit", await GitAsync(
            temporaryDirectory.Path, ["show", "HEAD:edited.txt"]));
        Assert.Equal("original", await GitAsync(
            temporaryDirectory.Path, ["show", "HEAD:staged.txt"]));
        Assert.Equal("staged.txt", await GitAsync(
            temporaryDirectory.Path, ["diff", "--cached", "--name-only"]));
        Assert.Equal("unstaged.txt", await GitAsync(
            temporaryDirectory.Path, ["diff", "--name-only"]));
        var metadata = await GitAsync(
            temporaryDirectory.Path,
            ["show", "-s", "--format=%an|%ae|%s%n%b", "HEAD"]);
        Assert.Contains("lumen|lumen@deckwraith.local|Adapt the project", metadata);
        Assert.Contains("Deckwraith-Wraith: lumen", metadata);
        Assert.Contains("Deckwraith-Haunt: compiler-lab", metadata);
        Assert.Null(commit.Warning);
    }

    [Fact]
    public async Task CreatesTheInitialCommitInAnUnbornRepository()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await InitializeProjectAsync(temporaryDirectory.Path);
        var batch = new AtomicFileEditBatch(
            [new("first.txt", FileEditKind.Write, Text: "first\n")],
            temporaryDirectory.Path,
            "Begin the project");
        var committer = new GitProjectCommitter();
        var preparation = await committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: false),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            batch.CommitSubject!,
            batch.CommitBody,
            AtomicFileEditor.ResolvePaths(batch),
            CancellationToken.None);

        var edit = await AtomicFileEditor.ApplyAsync(
            batch,
            (files, cancellationToken) => committer.CommitAsync(
                preparation, files, cancellationToken));

        var commit = Assert.IsType<ProjectCommitReceipt>(edit.Commit);
        Assert.Equal(commit.CommitId, await GitAsync(
            temporaryDirectory.Path, ["rev-parse", "HEAD"]));
        Assert.Equal("1", await GitAsync(
            temporaryDirectory.Path, ["rev-list", "--count", "HEAD"]));
        Assert.Equal("first", await GitAsync(
            temporaryDirectory.Path, ["show", "HEAD:first.txt"]));
        Assert.Equal(string.Empty, await GitAsync(
            temporaryDirectory.Path, ["status", "--porcelain=v1"]));
    }

    [Fact]
    public async Task CommitsANativeEquivalentPathWithDifferentCasing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await InitializeProjectAsync(temporaryDirectory.Path);
        var actualPath = Path.Combine(temporaryDirectory.Path, "MixedCase.txt");
        var alternatePath = Path.Combine(temporaryDirectory.Path, "mixedcase.txt");
        await File.WriteAllTextAsync(actualPath, "original\n");
        if (!File.Exists(alternatePath))
        {
            return;
        }

        await GitAsync(temporaryDirectory.Path, ["add", "--all"]);
        await GitAsync(temporaryDirectory.Path, ["commit", "-m", "baseline"]);
        var batch = new AtomicFileEditBatch(
            [new("mixedcase.txt", FileEditKind.Append, Text: "wraith edit\n")],
            temporaryDirectory.Path,
            "Respect native path identity");
        var committer = new GitProjectCommitter();
        var preparation = await committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: false),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            batch.CommitSubject!,
            batch.CommitBody,
            AtomicFileEditor.ResolvePaths(batch),
            CancellationToken.None);

        var edit = await AtomicFileEditor.ApplyAsync(
            batch,
            (files, cancellationToken) => committer.CommitAsync(
                preparation, files, cancellationToken));

        Assert.IsType<ProjectCommitReceipt>(edit.Commit);
        Assert.Equal("original\nwraith edit", await GitAsync(
            temporaryDirectory.Path, ["show", "HEAD:MixedCase.txt"]));
        Assert.Equal(string.Empty, await GitAsync(
            temporaryDirectory.Path, ["status", "--porcelain=v1"]));
    }

    [Fact]
    public async Task RefPublicationFailureLeavesTheBranchAndEditBatchUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await InitializeProjectAsync(temporaryDirectory.Path);
        var editedPath = Path.Combine(temporaryDirectory.Path, "edited.txt");
        await File.WriteAllTextAsync(editedPath, "original\n");
        await GitAsync(temporaryDirectory.Path, ["add", "--all"]);
        await GitAsync(temporaryDirectory.Path, ["commit", "-m", "baseline"]);
        var headBefore = await GitAsync(temporaryDirectory.Path, ["rev-parse", "HEAD"]);
        var batch = new AtomicFileEditBatch(
            [new("edited.txt", FileEditKind.Append, Text: "wraith edit\n")],
            temporaryDirectory.Path,
            "This ref update will fail");
        var committer = new GitProjectCommitter();
        var preparation = await committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: false),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            batch.CommitSubject!,
            batch.CommitBody,
            AtomicFileEditor.ResolvePaths(batch),
            CancellationToken.None);
        var gitDirectory = await GitAsync(
            temporaryDirectory.Path, ["rev-parse", "--absolute-git-dir"]);
        var refLock = Path.Combine(gitDirectory, "refs", "heads", "main.lock");
        await File.WriteAllTextAsync(refLock, "deliberate lock contention");

        try
        {
            var error = await Assert.ThrowsAsync<AtomicFileEditException>(() =>
                AtomicFileEditor.ApplyAsync(
                    batch,
                    (files, cancellationToken) => committer.CommitAsync(
                        preparation, files, cancellationToken)));
            Assert.Contains("all published files were restored", error.Message, StringComparison.Ordinal);
            Assert.IsType<ProjectCommitException>(error.InnerException);
        }
        finally
        {
            File.Delete(refLock);
        }

        Assert.Equal("original\n", await File.ReadAllTextAsync(editedPath));
        Assert.Equal(headBefore, await GitAsync(
            temporaryDirectory.Path, ["rev-parse", "HEAD"]));
        Assert.Equal(string.Empty, await GitAsync(
            temporaryDirectory.Path, ["status", "--porcelain=v1"]));
        Assert.Empty(Directory.EnumerateFiles(
            temporaryDirectory.Path, ".deckwraith-edit-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreflightRejectsDirtyTreesAndPathsOutsideTheAllowedScope()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await InitializeProjectAsync(temporaryDirectory.Path);
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "src"));
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "docs"));
        var targetPath = Path.Combine(temporaryDirectory.Path, "src", "target.txt");
        var unrelatedPath = Path.Combine(temporaryDirectory.Path, "docs", "notes.txt");
        await File.WriteAllTextAsync(targetPath, "original\n");
        await File.WriteAllTextAsync(unrelatedPath, "original\n");
        await GitAsync(temporaryDirectory.Path, ["add", "--all"]);
        await GitAsync(temporaryDirectory.Path, ["commit", "-m", "baseline"]);
        await File.WriteAllTextAsync(unrelatedPath, "dirty\n");
        var committer = new GitProjectCommitter();

        var dirty = await Assert.ThrowsAsync<ProjectCommitException>(() => committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: false, allowedPaths: ["src"]),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            "Edit target",
            null,
            [targetPath],
            CancellationToken.None));
        Assert.Contains("already has changes", dirty.Message, StringComparison.Ordinal);
        Assert.Equal("original\n", await File.ReadAllTextAsync(targetPath));

        var scope = await Assert.ThrowsAsync<ProjectCommitException>(() => committer.PrepareAsync(
            Policy(temporaryDirectory.Path, allowDirty: true, allowedPaths: ["docs"]),
            CanonicalName.Parse("lumen"),
            CanonicalName.Parse("compiler-lab"),
            "Edit target",
            null,
            [targetPath],
            CancellationToken.None));
        Assert.Contains("allowed project scopes", scope.Message, StringComparison.Ordinal);
        Assert.Equal("1", await GitAsync(
            temporaryDirectory.Path, ["rev-list", "--count", "HEAD"]));
    }

    private static HauntProjectPolicy Policy(
        string path,
        bool allowDirty,
        IReadOnlyList<string>? allowedPaths = null) => new(
        path,
        AutoCommitEnabled: true,
        ProjectCommitAuthor.ForWraith(),
        allowedPaths ?? ["."],
        allowDirty);

    private static async Task InitializeProjectAsync(string path)
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
}
