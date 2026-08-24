using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Application.Inference;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Runs;
using Deckwraith.Core.Serialization;

namespace Deckwraith.Continuity;

public sealed record RecoveryIncident(
    int SchemaVersion,
    string IncidentId,
    string Agent,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> OutcomeUnknownOperationIds,
    bool ContextRebuilt,
    IReadOnlyList<string> RecoveredRunIds,
    IReadOnlyList<string> AtomicWriteResidues,
    string EvidenceArchiveHash);

public sealed record RecoveryResult(
    RecoveryIncident? Incident,
    CurrentContextDocument Context,
    IReadOnlyList<RunDocument> Runs,
    string CommitId);

public sealed class RecoveryRuntime
{
    private const int IncidentSchemaVersion = 1;
    private readonly string _rootPath;
    private readonly IDeckStateStore _deckState;
    private readonly IInferenceStateStore _inferenceState;
    private readonly IAgentArchive _archive;
    private readonly ICompactionStore _compactions;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;

    public RecoveryRuntime(
        string rootPath,
        IDeckStateStore deckState,
        IInferenceStateStore inferenceState,
        IAgentArchive archive,
        ICompactionStore compactions,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _deckState = deckState;
        _inferenceState = inferenceState;
        _archive = archive;
        _compactions = compactions;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<RecoveryResult> RecoverAsync(
        string wraith,
        CancellationToken cancellationToken = default)
    {
        var agent = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var identity = await _deckState.ReadIdentityAsync(agent, cancellationToken)
            .ConfigureAwait(false);
        var records = await _archive.ReadAllAsync(agent, cancellationToken).ConfigureAwait(false);
        var compactions = await _compactions.ReadAllAsync(agent, cancellationToken)
            .ConfigureAwait(false);
        CompactionCoverage.ValidateExisting(
            compactions.Where(compaction => compaction.IsValid).ToArray(), records);

        var terminals = records.Where(record =>
                record.Payload.ValueKind is JsonValueKind.Object &&
                record.Payload.TryGetProperty("operationId", out var operationId) &&
                operationId.ValueKind is JsonValueKind.String &&
                IsTerminal(record.Kind))
            .Select(record => record.Payload.GetProperty("operationId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var abandoned = records.Where(record =>
                record.Payload.ValueKind is JsonValueKind.Object &&
                record.Payload.TryGetProperty("operationId", out var operationId) &&
                operationId.ValueKind is JsonValueKind.String &&
                IsStarted(record.Kind) &&
                !terminals.Contains(operationId.GetString()!))
            .ToArray();
        var outcomeUnknown = new List<string>();
        foreach (var started in abandoned)
        {
            var operationId = started.Payload.GetProperty("operationId").GetString()!;
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    OutcomeUnknownKind(started.Kind),
                    CanonicalJson.ToElement(new
                    {
                        operationId,
                        startedKind = started.Kind,
                        startedSequence = started.Sequence,
                        status = OperationStatus.OutcomeUnknown,
                        replayed = false,
                        reason = "startup-reconciliation",
                    }),
                    started.Haunt,
                    started.RunId,
                    started.ShellId,
                    Timestamp: _clock.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            outcomeUnknown.Add(operationId);
        }

        records = await _archive.ReadAllAsync(agent, CancellationToken.None).ConfigureAwait(false);
        var rebuilt = ContextArchiveRebuilder.Rebuild(
            agent,
            records,
            CanonicalJson.Hash(identity),
            ReadToolElisionTurns(records),
            _clock.UtcNow);
        var contextRebuilt = false;
        try
        {
            var current = await _inferenceState.ReadContextAsync(agent, cancellationToken)
                .ConfigureAwait(false);
            contextRebuilt = !StringComparer.Ordinal.Equals(
                CanonicalJson.Hash(current), CanonicalJson.Hash(rebuilt));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            contextRebuilt = true;
        }

        if (contextRebuilt)
        {
            await _inferenceState.ReplaceContextAsync(
                agent, rebuilt, CancellationToken.None).ConfigureAwait(false);
        }

        var runs = (await _inferenceState.ListRunsAsync(agent, cancellationToken)
            .ConfigureAwait(false)).ToArray();
        var recoveredRuns = new List<string>();
        for (var index = 0; index < runs.Length; index++)
        {
            var run = runs[index];
            if (run.Status is not RunStatus.Running)
            {
                continue;
            }

            var active = run.Shells[^1];
            var ended = active with
            {
                EndedAt = _clock.UtcNow,
                EndReason = "startup-recovery-outcome-unknown",
            };
            var replacement = new ShellDocument(
                Guid.CreateVersion7(_clock.UtcNow).ToString("N"),
                active.Provider,
                active.Model,
                _clock.UtcNow,
                null,
                null);
            var shells = run.Shells.ToArray();
            shells[^1] = ended;
            run = run with
            {
                Status = RunStatus.AwaitingInput,
                StatusReason = "startup-recovered-cold-shell",
                Shells = [.. shells, replacement],
                UpdatedAt = _clock.UtcNow,
            };
            await _inferenceState.WriteRunAsync(agent, run, CancellationToken.None)
                .ConfigureAwait(false);
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "shell.ended",
                    CanonicalJson.ToElement(new
                    {
                        ended.ShellId,
                        ended.Provider,
                        ended.Model,
                        ended.EndedAt,
                        ended.EndReason,
                    }),
                    run.Haunt,
                    run.RunId,
                    ended.ShellId,
                    Timestamp: _clock.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "shell.started",
                    CanonicalJson.ToElement(new
                    {
                        replacement.ShellId,
                        replacement.Provider,
                        replacement.Model,
                        previousShellId = ended.ShellId,
                        reason = "startup-recovery",
                        replayedCommands = false,
                    }),
                    run.Haunt,
                    run.RunId,
                    replacement.ShellId,
                    Timestamp: _clock.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "run.recovered",
                    CanonicalJson.ToElement(new
                    {
                        run.RunId,
                        status = run.Status,
                        run.StatusReason,
                        previousShellId = ended.ShellId,
                        shellId = replacement.ShellId,
                    }),
                    run.Haunt,
                    run.RunId,
                    replacement.ShellId,
                    Timestamp: _clock.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            runs[index] = run;
            recoveredRuns.Add(run.RunId);
        }

        var residues = FindAtomicWriteResidues();
        RecoveryIncident? incident = null;
        if (outcomeUnknown.Count > 0 || contextRebuilt || recoveredRuns.Count > 0 || residues.Count > 0)
        {
            records = await _archive.ReadAllAsync(agent, CancellationToken.None).ConfigureAwait(false);
            incident = new RecoveryIncident(
                IncidentSchemaVersion,
                Guid.CreateVersion7(_clock.UtcNow).ToString("N"),
                agent.Value,
                _clock.UtcNow,
                outcomeUnknown,
                contextRebuilt,
                recoveredRuns,
                residues,
                records.Count == 0 ? "empty" : records[^1].ContentHash);
            await WriteIncidentAsync(incident, CancellationToken.None).ConfigureAwait(false);
            await _archive.AppendAsync(
                new ArchiveEvent(
                    agent.Value,
                    "recovery.completed",
                    CanonicalJson.ToElement(new { incident }),
                    Timestamp: _clock.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }

        var commit = await _checkpoints.CheckpointAsync(
            incident is null ? "startup-validated" : "startup-recovered",
            agent,
            null,
            CancellationToken.None).ConfigureAwait(false);
        return new RecoveryResult(incident, rebuilt, runs, commit);
    }

    private static int ReadToolElisionTurns(IReadOnlyList<ArchiveRecord> records)
    {
        var committed = records.LastOrDefault(record =>
            StringComparer.Ordinal.Equals(record.Kind, "context.committed"));
        if (committed is not null &&
            committed.Payload.TryGetProperty("context", out var context) &&
            context.TryGetProperty("toolElisionTurns", out var turns) &&
            turns.TryGetInt32(out var value) &&
            value >= 0)
        {
            return value;
        }

        return 8;
    }

    private List<string> FindAtomicWriteResidues()
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) &&
                (Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal) ||
                 Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(_rootPath, path))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task WriteIncidentAsync(
        RecoveryIncident incident,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_rootPath, "recovery", "incidents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, incident.IncidentId + ".json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporary, CanonicalJson.Serialize(incident), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsStarted(string kind) =>
        kind.EndsWith(".started", StringComparison.Ordinal) ||
        kind.EndsWith("-started", StringComparison.Ordinal);

    private static bool IsTerminal(string kind) =>
        kind.EndsWith(".completed", StringComparison.Ordinal) ||
        kind.EndsWith(".failed", StringComparison.Ordinal) ||
        kind.EndsWith(".cancelled", StringComparison.Ordinal) ||
        kind.EndsWith(".outcome-unknown", StringComparison.Ordinal) ||
        kind.EndsWith("-completed", StringComparison.Ordinal) ||
        kind.EndsWith("-failed", StringComparison.Ordinal) ||
        kind.EndsWith("-cancelled", StringComparison.Ordinal) ||
        kind.EndsWith("-outcome-unknown", StringComparison.Ordinal) ||
        StringComparer.Ordinal.Equals(kind, "compaction.accepted");

    private static string OutcomeUnknownKind(string startedKind) =>
        startedKind.EndsWith(".started", StringComparison.Ordinal)
            ? startedKind[..^".started".Length] + ".outcome-unknown"
            : startedKind[..^"-started".Length] + "-outcome-unknown";
}
