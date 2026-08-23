using System.Diagnostics;
using System.Text;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Persistence.Git;

public sealed class GitCheckpointStore : ICheckpointStore
{
    private readonly string _rootPath;

    public GitCheckpointStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task InitializeRepositoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);
        SensitiveFilePermissions.RestrictDirectory(_rootPath);
        if (!Directory.Exists(Path.Combine(_rootPath, ".git")))
        {
            await RunProcessAsync(
                "git",
                ["init", "--initial-branch=main", "--", _rootPath],
                cancellationToken).ConfigureAwait(false);
        }

        var remotes = await RunGitAsync(["remote"], cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(remotes))
        {
            throw new DeckStateException(
                "Deck initialization refuses an existing Git repository with remotes; state is credential-equivalent data.");
        }

        SensitiveFilePermissions.RestrictTree(_rootPath);
    }

    public async Task<string> CheckpointAsync(
        string reason,
        CanonicalName? wraith,
        CanonicalName? haunt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await RunGitAsync(["add", "--all", "--", "."], cancellationToken).ConfigureAwait(false);
        var staged = await RunGitAsync(
            ["diff", "--cached", "--quiet"], allowExitCodeOne: true, cancellationToken)
            .ConfigureAwait(false);
        if (staged.ExitCode == 0)
        {
            return (await RunGitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Trim();
        }

        var subject = $"deckwraith: checkpoint {wraith?.Value ?? "deck"} {reason}";
        var trailers = new StringBuilder();
        if (wraith is { } agentName)
        {
            trailers.AppendLine("Deckwraith-Agent: " + agentName.Value);
        }

        if (haunt is { } hauntName)
        {
            trailers.AppendLine("Deckwraith-Haunt: " + hauntName.Value);
        }

        trailers.Append("Deckwraith-Reason: ").Append(reason);
        await RunGitAsync(
            ["commit", "--no-gpg-sign", "-m", subject, "-m", trailers.ToString()],
            cancellationToken).ConfigureAwait(false);
        SensitiveFilePermissions.RestrictTree(_rootPath);
        return (await RunGitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Trim();
    }

    private async Task<string> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(arguments, allowExitCodeOne: false, cancellationToken)
            .ConfigureAwait(false);
        return result.Output;
    }

    private Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        bool allowExitCodeOne,
        CancellationToken cancellationToken)
    {
        var allArguments = new List<string> { "-C", _rootPath };
        allArguments.AddRange(arguments);
        return RunProcessAsync("git", allArguments, cancellationToken, allowExitCodeOne);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowExitCodeOne = false)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_AUTHOR_NAME"] = "Deckwraith";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "deckwraith@localhost";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "Deckwraith";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "deckwraith@localhost";
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new DeckStateException($"Could not start {executable}.");
        }

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
        if (process.ExitCode != 0 && !(allowExitCodeOne && process.ExitCode == 1))
        {
            throw new DeckStateException(
                $"{executable} exited with code {process.ExitCode}: {error.Trim()}");
        }

        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
