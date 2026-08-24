using System.Diagnostics;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.State;

namespace Deckwraith.Continuity;

public sealed record GitReversalResult(
    string ReversedCommit,
    string PreviousHead,
    string NewHead,
    string RecoveryBranch,
    string Warning);

public sealed class GitReversalRuntime
{
    private readonly string _rootPath;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;

    public GitReversalRuntime(
        string rootPath,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<GitReversalResult> ReverseCommitAsync(
        string commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        var status = await RunGitAsync(
            ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            throw new DeckStateException(
                "Non-destructive reversal requires a clean state repository.");
        }

        var resolved = (await RunGitAsync(
            ["rev-parse", "--verify", commit + "^{commit}"], cancellationToken)
            .ConfigureAwait(false)).Output.Trim();
        var previousHead = (await RunGitAsync(
            ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Output.Trim();
        var ancestor = await RunGitAsync(
            ["merge-base", "--is-ancestor", resolved, previousHead],
            cancellationToken,
            allowExitCodeOne: true).ConfigureAwait(false);
        if (ancestor.ExitCode != 0)
        {
            throw new DeckStateException(
                $"Commit '{resolved}' is not an ancestor of the current state.");
        }

        var recoveryBranch = $"deckwraith/recovery/" +
            $"{_clock.UtcNow:yyyyMMdd-HHmmss}-{previousHead[..8]}-" +
            Guid.NewGuid().ToString("N")[..8];
        await RunGitAsync(
            ["branch", recoveryBranch, previousHead], cancellationToken).ConfigureAwait(false);
        var revert = await RunGitAsync(
            ["revert", "--no-commit", resolved],
            cancellationToken,
            allowExitCodeOne: true).ConfigureAwait(false);
        if (revert.ExitCode != 0)
        {
            throw new DeckStateException(
                $"Git could not apply the inverse of '{resolved}': {revert.Error.Trim()}");
        }

        var newHead = await _checkpoints.CheckpointAsync(
            "non-destructive-reversal-" + resolved[..12],
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return new GitReversalResult(
            resolved,
            previousHead,
            newHead,
            recoveryBranch,
            "The state commit was inverted. External side effects were not and cannot be reversed by Git.");
    }

    private async Task<GitResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowExitCodeOne = false)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _rootPath,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new DeckStateException("Could not start Git for recovery.");
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var result = new GitResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
        if (result.ExitCode != 0 && !(allowExitCodeOne && result.ExitCode == 1))
        {
            throw new DeckStateException(
                $"Git exited with code {result.ExitCode}: {result.Error.Trim()}");
        }

        return result;
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
