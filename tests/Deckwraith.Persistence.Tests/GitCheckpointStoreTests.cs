using System.Diagnostics;
using Deckwraith.Persistence.Git;

namespace Deckwraith.Persistence.Tests;

public sealed class GitCheckpointStoreTests
{
    [Fact]
    public async Task InitializationKeepsAutomaticMaintenanceInTheForeground()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var checkpoints = new GitCheckpointStore(temporaryDirectory.Path);

        await checkpoints.InitializeRepositoryAsync(CancellationToken.None);

        Assert.Equal("false", await ReadConfigAsync(
            temporaryDirectory.Path, "gc.autoDetach"));
        Assert.Equal("false", await ReadConfigAsync(
            temporaryDirectory.Path, "maintenance.autoDetach"));
    }

    private static async Task<string> ReadConfigAsync(string rootPath, string key)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--local");
        startInfo.ArgumentList.Add("--get");
        startInfo.ArgumentList.Add(key);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Git.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
}
