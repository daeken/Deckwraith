using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Json;

namespace Deckwraith.Persistence.State;

public sealed class JsonInferenceStateStore : IInferenceStateStore
{
    private readonly string _rootPath;

    public JsonInferenceStateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<CurrentContextDocument> EnsureContextAsync(
        CanonicalName wraith,
        string identityHash,
        int toolElisionTurns,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(toolElisionTurns);
        var path = ContextPath(wraith);
        if (File.Exists(path))
        {
            return await ReadContextAsync(wraith, cancellationToken).ConfigureAwait(false);
        }

        EnsureWraithExists(wraith);
        var context = CurrentContextDocument.Create(wraith, identityHash, toolElisionTurns, now);
        await AtomicJsonFile.WriteAsync(path, context, cancellationToken).ConfigureAwait(false);
        return context;
    }

    public async Task<CurrentContextDocument> ReadContextAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken)
    {
        var context = await AtomicJsonFile.ReadAsync<CurrentContextDocument>(
            ContextPath(wraith), cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(context.Agent, wraith.Value))
        {
            throw new DeckStateException(
                $"Context agent '{context.Agent}' does not match its path '{wraith}'.");
        }

        return context;
    }

    public async Task WriteContextAsync(
        CanonicalName wraith,
        CurrentContextDocument context,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(context.Agent, wraith.Value))
        {
            throw new DeckStateException(
                $"Cannot write context for '{context.Agent}' under '{wraith}'.");
        }

        var current = await ReadContextAsync(wraith, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
        {
            throw new DeckStateException(
                $"Context revision conflict for '{wraith}': expected {expectedRevision}, found {current.Revision}.");
        }

        if (context.Revision != checked(expectedRevision + 1))
        {
            throw new DeckStateException(
                $"Context revision must advance exactly once from {expectedRevision}.");
        }

        if (context.ArchiveFrontier < current.ArchiveFrontier || context.Turn < current.Turn)
        {
            throw new DeckStateException("Context archive frontier and turn cannot move backward.");
        }

        await AtomicJsonFile.WriteAsync(
            ContextPath(wraith), context, cancellationToken).ConfigureAwait(false);
    }

    public Task ReplaceContextAsync(
        CanonicalName wraith,
        CurrentContextDocument context,
        CancellationToken cancellationToken)
    {
        EnsureWraithExists(wraith);
        if (!StringComparer.Ordinal.Equals(context.Agent, wraith.Value))
        {
            throw new DeckStateException(
                $"Cannot replace context for '{context.Agent}' under '{wraith}'.");
        }

        return AtomicJsonFile.WriteAsync(
            ContextPath(wraith), context, cancellationToken);
    }

    public async Task CreateRunAsync(
        CanonicalName wraith,
        RunDocument run,
        CancellationToken cancellationToken)
    {
        EnsureWraithExists(wraith);
        ValidateRunIdentity(wraith, run);
        foreach (var runPath in Directory.EnumerateFiles(
            RunsPath(wraith), "run.json", SearchOption.AllDirectories))
        {
            var existing = await AtomicJsonFile.ReadAsync<RunDocument>(runPath, cancellationToken)
                .ConfigureAwait(false);
            if (existing.Status is RunStatus.Created or RunStatus.Running or
                RunStatus.AwaitingInput or RunStatus.Blocked)
            {
                throw new DeckStateException(
                    $"Wraith '{wraith}' already has active run '{existing.RunId}'.");
            }
        }

        var directory = RunPath(wraith, run.RunId);
        if (Directory.Exists(directory))
        {
            throw new DeckStateException($"Run '{run.RunId}' already exists for '{wraith}'.");
        }

        Directory.CreateDirectory(Path.Combine(directory, "state", "values"));
        SensitiveFilePermissions.RestrictDirectory(directory);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(directory, "run.json"), run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunDocument> ReadRunAsync(
        CanonicalName wraith,
        string runId,
        CancellationToken cancellationToken)
    {
        ValidateRunId(runId);
        var run = await AtomicJsonFile.ReadAsync<RunDocument>(
            Path.Combine(RunPath(wraith, runId), "run.json"), cancellationToken).ConfigureAwait(false);
        ValidateRunIdentity(wraith, run);
        return run;
    }

    public async Task WriteRunAsync(
        CanonicalName wraith,
        RunDocument run,
        CancellationToken cancellationToken)
    {
        ValidateRunIdentity(wraith, run);
        var path = Path.Combine(RunPath(wraith, run.RunId), "run.json");
        if (!File.Exists(path))
        {
            throw new DeckStateException($"Run '{run.RunId}' does not exist for '{wraith}'.");
        }

        await AtomicJsonFile.WriteAsync(path, run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunDocument>> ListRunsAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken)
    {
        EnsureWraithExists(wraith);
        var runs = new List<RunDocument>();
        foreach (var path in Directory.EnumerateFiles(
            RunsPath(wraith), "run.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var run = await AtomicJsonFile.ReadAsync<RunDocument>(path, cancellationToken)
                .ConfigureAwait(false);
            ValidateRunIdentity(wraith, run);
            runs.Add(run);
        }

        return runs.OrderBy(run => run.CreatedAt).ThenBy(run => run.RunId, StringComparer.Ordinal)
            .ToArray();
    }

    private string ContextPath(CanonicalName wraith) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "context.json");

    private string RunsPath(CanonicalName wraith) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "runs");

    private string RunPath(CanonicalName wraith, string runId)
    {
        ValidateRunId(runId);
        return Path.Combine(RunsPath(wraith), runId);
    }

    private void EnsureWraithExists(CanonicalName wraith)
    {
        if (!Directory.Exists(Path.Combine(_rootPath, "agents", wraith.Value)))
        {
            throw new DeckStateException($"The wraith '{wraith}' does not exist.");
        }
    }

    private static void ValidateRunIdentity(CanonicalName wraith, RunDocument run)
    {
        ValidateRunId(run.RunId);
        if (!StringComparer.Ordinal.Equals(wraith.Value, run.Agent))
        {
            throw new DeckStateException(
                $"Run agent '{run.Agent}' does not match its path '{wraith}'.");
        }
    }

    private static void ValidateRunId(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
        {
            throw new ArgumentException("A run ID must be a compact GUID.", nameof(runId));
        }
    }
}
