using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Deckwraith.Application.Files;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Persistence.Git;

public sealed class GitProjectCommitter : IProjectCommitter
{
    private static readonly string[] OperationFileMarkers =
        ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG"];
    private static readonly string[] OperationDirectoryMarkers =
        ["rebase-merge", "rebase-apply", "sequencer"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryGates = new(
        PathComparer);

    public async Task<ProjectCommitPreparation> PrepareAsync(
        HauntProjectPolicy policy,
        CanonicalName wraith,
        CanonicalName haunt,
        string subject,
        string? body,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(targetPaths);
        if (subject.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ProjectCommitException(
                "A project commit subject must be one line and cannot contain null characters.");
        }

        if (body?.Contains('\0') is true)
        {
            throw new ProjectCommitException(
                "A project commit body cannot contain null characters.");
        }

        if (targetPaths.Count == 0)
        {
            throw new ProjectCommitException("A project commit must name at least one edited path.");
        }

        var projectPath = Path.GetFullPath(policy.ProjectPath);
        if (!Directory.Exists(projectPath))
        {
            throw new ProjectCommitException(
                $"Haunt project directory '{projectPath}' does not exist.");
        }

        var repositoryPath = Path.GetFullPath((await RunGitAsync(
            projectPath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false)).Output.Trim());
        var projectPrefix = (await RunGitAsync(
            projectPath,
            ["rev-parse", "--show-prefix"],
            cancellationToken).ConfigureAwait(false)).Output.Trim();
        var branch = await RunGitAsync(
            repositoryPath,
            ["symbolic-ref", "--quiet", "HEAD"],
            cancellationToken,
            allowedExitCodes: [0, 1]).ConfigureAwait(false);
        if (branch.ExitCode != 0)
        {
            throw new ProjectCommitException(
                $"Project repository '{repositoryPath}' has a detached HEAD; auto-commit requires a branch.");
        }

        var gitDirectory = Path.GetFullPath((await RunGitAsync(
            repositoryPath,
            ["rev-parse", "--absolute-git-dir"],
            cancellationToken).ConfigureAwait(false)).Output.Trim());
        if (RepositoryOperationInProgress(gitDirectory))
        {
            throw new ProjectCommitException(
                $"Project repository '{repositoryPath}' has a Git operation in progress.");
        }

        var unmerged = await RunGitAsync(
            repositoryPath,
            ["diff", "--name-only", "--diff-filter=U"],
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(unmerged.Output))
        {
            throw new ProjectCommitException(
                $"Project repository '{repositoryPath}' has unresolved merge conflicts.");
        }

        var allowedRoots = ResolveAllowedRoots(policy, projectPath);
        var normalizedTargets = targetPaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
        foreach (var target in normalizedTargets)
        {
            if (!IsWithin(projectPath, target))
            {
                throw new ProjectCommitException(
                    $"Edited path '{target}' is outside haunt project '{projectPath}'.");
            }

            if (!allowedRoots.Any(scope => IsWithin(scope, target)))
            {
                throw new ProjectCommitException(
                    $"Edited path '{target}' is outside the haunt's allowed project scopes.");
            }
        }

        var repositoryRelativePaths = normalizedTargets
            .Select(target => Path.Combine(
                projectPrefix,
                Path.GetRelativePath(projectPath, target)).Replace('\\', '/'))
            .ToArray();
        if (repositoryRelativePaths.Any(path =>
            path == ".." || path.StartsWith("../", StringComparison.Ordinal)))
        {
            throw new ProjectCommitException(
                "An approved edit path is outside the project repository.");
        }

        var targetStatus = await RunGitAsync(
            repositoryPath,
            [
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all",
                "--",
                .. repositoryRelativePaths,
            ],
            cancellationToken,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_LITERAL_PATHSPECS"] = "1",
            }).ConfigureAwait(false);
        if (targetStatus.Output.Length > 0)
        {
            throw new ProjectCommitException(
                "An edited path already has changes; auto-commit will not absorb pre-existing work on its target paths.");
        }

        if (!policy.AllowDirtyWorkingTree)
        {
            var status = await RunGitAsync(
                repositoryPath,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status.Output))
            {
                throw new ProjectCommitException(
                    $"Project repository '{repositoryPath}' already has changes and this haunt does not permit a dirty working tree.");
            }
        }

        var (authorName, authorEmail) = ResolveAuthor(policy.Author, wraith);
        return new ProjectCommitPreparation(
            projectPath,
            repositoryPath,
            wraith,
            haunt,
            subject.Trim(),
            string.IsNullOrWhiteSpace(body) ? null : body,
            authorName,
            authorEmail,
            normalizedTargets,
            repositoryRelativePaths);
    }

    public async Task<ProjectCommitReceipt?> CommitAsync(
        ProjectCommitPreparation preparation,
        IReadOnlyList<FileEditReceipt> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(files);
        var receipts = files.OrderBy(file => file.Path, PathComparer).ToArray();
        if (!receipts.Select(file => Path.GetFullPath(file.Path)).SequenceEqual(
            preparation.TargetPaths, PathComparer))
        {
            throw new ProjectCommitException(
                "The successful edit receipt does not match the paths approved for auto-commit.");
        }

        var gate = RepositoryGates.GetOrAdd(
            preparation.RepositoryPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyReceiptsAsync(receipts, cancellationToken).ConfigureAwait(false);
            return await CommitUnderLockAsync(preparation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ProjectCommitReceipt?> CommitUnderLockAsync(
        ProjectCommitPreparation preparation,
        CancellationToken cancellationToken)
    {
        var relativePaths = preparation.RepositoryRelativePaths;

        var temporaryIndex = Path.Combine(
            Path.GetTempPath(), $"deckwraith-project-index-{Guid.NewGuid():N}");
        var temporaryHooks = Path.Combine(
            Path.GetTempPath(), $"deckwraith-project-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryHooks);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = temporaryIndex,
            ["GIT_LITERAL_PATHSPECS"] = "1",
            ["GIT_AUTHOR_NAME"] = preparation.AuthorName,
            ["GIT_AUTHOR_EMAIL"] = preparation.AuthorEmail,
            ["GIT_COMMITTER_NAME"] = "Deckwraith",
            ["GIT_COMMITTER_EMAIL"] = "deckwraith@localhost",
        };
        try
        {
            var branch = (await RunGitAsync(
                preparation.RepositoryPath,
                ["symbolic-ref", "--quiet", "HEAD"],
                cancellationToken,
                environment).ConfigureAwait(false)).Output.Trim();
            var head = await RunGitAsync(
                preparation.RepositoryPath,
                ["rev-parse", "--verify", "HEAD"],
                cancellationToken,
                environment,
                allowedExitCodes: [0, 128]).ConfigureAwait(false);
            await RunGitAsync(
                preparation.RepositoryPath,
                head.ExitCode == 0
                    ? ["read-tree", head.Output.Trim()]
                    : ["read-tree", "--empty"],
                cancellationToken,
                environment).ConfigureAwait(false);
            await RunGitAsync(
                preparation.RepositoryPath,
                ["add", "--", .. relativePaths],
                cancellationToken,
                environment).ConfigureAwait(false);
            await VerifyPathsAsync(
                preparation.TargetPaths,
                preparation.RepositoryRelativePaths,
                preparation.RepositoryPath,
                environment,
                cancellationToken).ConfigureAwait(false);
            var treeId = (await RunGitAsync(
                preparation.RepositoryPath,
                ["write-tree"],
                cancellationToken,
                environment).ConfigureAwait(false)).Output.Trim();
            var parentTreeId = head.ExitCode == 0
                ? (await RunGitAsync(
                    preparation.RepositoryPath,
                    ["rev-parse", head.Output.Trim() + "^{tree}"],
                    cancellationToken,
                    environment).ConfigureAwait(false)).Output.Trim()
                : null;
            if (StringComparer.Ordinal.Equals(treeId, parentTreeId))
            {
                return null;
            }

            var changedPathArguments = new List<string>
            {
                "diff",
                "--cached",
                "--name-only",
                "-z",
            };
            if (head.ExitCode == 0)
            {
                changedPathArguments.Add(head.Output.Trim());
            }

            changedPathArguments.Add("--");
            changedPathArguments.AddRange(relativePaths);
            var committedPaths = (await RunGitAsync(
                preparation.RepositoryPath,
                changedPathArguments,
                cancellationToken,
                environment).ConfigureAwait(false)).Output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (committedPaths.Length == 0 ||
                committedPaths.Any(path => !relativePaths.Contains(path, PathComparer)))
            {
                throw new ProjectCommitException(
                    "The proposed project commit does not match its edit receipt.");
            }

            var trailers = new StringBuilder()
                .AppendLine("Deckwraith-Wraith: " + preparation.Wraith.Value)
                .AppendLine("Deckwraith-Haunt: " + preparation.Haunt.Value)
                .Append("Deckwraith-Auto-Commit: true")
                .ToString();
            var arguments = new List<string>
            {
                "commit-tree",
                treeId,
            };
            if (head.ExitCode == 0)
            {
                arguments.Add("-p");
                arguments.Add(head.Output.Trim());
            }

            arguments.Add("-m");
            arguments.Add(preparation.Subject);
            if (preparation.Body is not null)
            {
                arguments.Add("-m");
                arguments.Add(preparation.Body);
            }

            arguments.Add("-m");
            arguments.Add(trailers);
            var commitId = (await RunGitAsync(
                preparation.RepositoryPath,
                arguments,
                cancellationToken,
                environment).ConfigureAwait(false)).Output.Trim();

            await RunGitAsync(
                preparation.RepositoryPath,
                [
                    "-c",
                    "core.hooksPath=" + temporaryHooks,
                    "update-ref",
                    "-m",
                    "Deckwraith auto-commit: " + preparation.Subject,
                    branch,
                    commitId,
                    head.ExitCode == 0 ? head.Output.Trim() : string.Empty,
                ],
                cancellationToken).ConfigureAwait(false);

            string? warning = null;
            try
            {
                await RunGitAsync(
                    preparation.RepositoryPath,
                    ["reset", "--quiet", "HEAD", "--", .. relativePaths],
                    CancellationToken.None,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["GIT_LITERAL_PATHSPECS"] = "1",
                    }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                warning =
                    "The commit succeeded, but Deckwraith could not align the existing index for its edited paths: " +
                    exception.Message;
            }

            return new ProjectCommitReceipt(
                preparation.RepositoryPath,
                commitId,
                preparation.Subject,
                preparation.AuthorName,
                preparation.AuthorEmail,
                committedPaths,
                warning);
        }
        finally
        {
            DeleteFileIfPresent(temporaryIndex);
            DeleteFileIfPresent(temporaryIndex + ".lock");
            try
            {
                Directory.Delete(temporaryHooks, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static (string Name, string Email) ResolveAuthor(
        ProjectCommitAuthor author,
        CanonicalName wraith)
    {
        if (author.Mode is ProjectCommitAuthorMode.Wraith)
        {
            return (wraith.Value, $"{wraith.Value}@deckwraith.local");
        }

        if (author.Mode is not ProjectCommitAuthorMode.Fixed ||
            string.IsNullOrWhiteSpace(author.Name) ||
            string.IsNullOrWhiteSpace(author.Email) ||
            author.Name.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            author.Email.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ProjectCommitException("The haunt's project commit author is invalid.");
        }

        return (author.Name.Trim(), author.Email.Trim());
    }

    private static string[] ResolveAllowedRoots(HauntProjectPolicy policy, string projectPath)
    {
        if (policy.AllowedPaths is null || policy.AllowedPaths.Count == 0)
        {
            throw new ProjectCommitException("The haunt has no allowed project path scopes.");
        }

        return policy.AllowedPaths.Select(scope =>
        {
            if (string.IsNullOrWhiteSpace(scope) || Path.IsPathRooted(scope))
            {
                throw new ProjectCommitException(
                    $"The haunt's allowed project path '{scope}' is invalid.");
            }

            var resolved = Path.GetFullPath(Path.Combine(projectPath, scope));
            if (!IsWithin(projectPath, resolved))
            {
                throw new ProjectCommitException(
                    $"The haunt's allowed project path '{scope}' escapes the project directory.");
            }

            return resolved;
        }).ToArray();
    }

    private static bool RepositoryOperationInProgress(string gitDirectory) =>
        OperationFileMarkers
            .Any(marker => File.Exists(Path.Combine(gitDirectory, marker))) ||
        OperationDirectoryMarkers
            .Any(marker => Directory.Exists(Path.Combine(gitDirectory, marker)));

    private static async Task VerifyReceiptsAsync(
        IReadOnlyList<FileEditReceipt> receipts,
        CancellationToken cancellationToken)
    {
        foreach (var file in receipts)
        {
            if (!File.Exists(file.Path))
            {
                throw new ProjectCommitException(
                    $"Edited file '{file.Path}' disappeared before it could be committed.");
            }

            var actualHash = Hash(await File.ReadAllBytesAsync(
                file.Path, cancellationToken).ConfigureAwait(false));
            if (!StringComparer.Ordinal.Equals(actualHash, file.AfterHash))
            {
                throw new ProjectCommitException(
                    $"Edited file '{file.Path}' changed before it could be committed.");
            }
        }
    }

    private static async Task VerifyPathsAsync(
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> repositoryRelativePaths,
        string repositoryPath,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < targetPaths.Count; index++)
        {
            var stagedEntry = (await RunGitAsync(
                repositoryPath,
                ["ls-files", "--stage", "-z", "--", repositoryRelativePaths[index]],
                cancellationToken,
                environment).ConfigureAwait(false)).Output;
            var metadataEnd = stagedEntry.IndexOf('\t');
            var metadata = metadataEnd < 0 ? [] : stagedEntry[..metadataEnd].Split(' ');
            if (metadata.Length < 3)
            {
                throw new ProjectCommitException(
                    $"Edited file '{targetPaths[index]}' was not staged for its project commit.");
            }

            var stagedObject = metadata[1];
            var workingObject = (await RunGitAsync(
                repositoryPath,
                [
                    "hash-object",
                    "--path=" + repositoryRelativePaths[index],
                    "--",
                    repositoryRelativePaths[index],
                ],
                cancellationToken).ConfigureAwait(false)).Output.Trim();
            if (!StringComparer.Ordinal.Equals(stagedObject, workingObject))
            {
                throw new ProjectCommitException(
                    $"Edited file '{targetPaths[index]}' changed while it was being staged.");
            }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    // Target paths come from the editor after native casing is resolved. Do not
    // collapse distinct paths merely because the host OS commonly uses a
    // case-insensitive volume; APFS and NTFS can both be case-sensitive.
    private static StringComparer PathComparer => StringComparer.Ordinal;

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<int>? allowedExitCodes = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo) ??
            throw new ProjectCommitException("Could not start Git for project auto-commit.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (!(allowedExitCodes ?? [0]).Contains(process.ExitCode))
        {
            throw new ProjectCommitException(
                $"Git exited with code {process.ExitCode}: {error.Trim()}");
        }

        return new GitResult(process.ExitCode, output);
    }

    private sealed record GitResult(int ExitCode, string Output);
}
