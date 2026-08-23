using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Json;

namespace Deckwraith.Persistence.State;

public sealed class JsonDurableValueStore : IDurableValueStore
{
    private readonly string _rootPath;
    private readonly string _lockPath;

    public JsonDurableValueStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _lockPath = Path.Combine(
            Path.GetTempPath(),
            "deckwraith-cas",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_rootPath))));
    }

    public async Task<DurableValueRecord?> ReadAsync(
        CanonicalName wraith,
        DurableValueScope scope,
        string name,
        string? runId,
        CanonicalName? haunt,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        var path = ValuePath(wraith, scope, name, runId, haunt);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = await AtomicJsonFile.ReadAsync<DurableValueRecord>(
            path, cancellationToken).ConfigureAwait(false);
        ValidateRecord(value, scope, name, runId, haunt);
        return value with { Value = value.Value.Clone() };
    }

    public async Task<IReadOnlyList<DurableValueRecord>> ListAsync(
        CanonicalName wraith,
        DurableValueScope scope,
        string? runId,
        CanonicalName? haunt,
        CancellationToken cancellationToken)
    {
        var directory = ScopePath(wraith, scope, runId, haunt);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var values = new List<DurableValueRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var value = await AtomicJsonFile.ReadAsync<DurableValueRecord>(
                path, cancellationToken).ConfigureAwait(false);
            ValidateRecord(value, scope, value.Name, runId, haunt);
            values.Add(value with { Value = value.Value.Clone() });
        }

        return values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<DurableValueRecord> WriteAsync(
        CanonicalName wraith,
        DurableValueScope scope,
        string name,
        JsonElement value,
        string? runId,
        CanonicalName? haunt,
        long? expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        ValidateJson(value);
        var path = ValuePath(wraith, scope, name, runId, haunt);
        await using var lease = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        var current = File.Exists(path)
            ? await AtomicJsonFile.ReadAsync<DurableValueRecord>(
                path, cancellationToken).ConfigureAwait(false)
            : null;
        EnsureExpectedVersion(name, expectedVersion, current?.Version ?? 0);
        var record = new DurableValueRecord(
            DurableValueRecord.CurrentSchemaVersion,
            scope,
            name,
            value.Clone(),
            CanonicalJson.Hash(value),
            wraith.Value,
            runId,
            haunt?.Value,
            checked((current?.Version ?? 0) + 1),
            now);
        await AtomicJsonFile.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<DurableValueRecord?> RemoveAsync(
        CanonicalName wraith,
        DurableValueScope scope,
        string name,
        string? runId,
        CanonicalName? haunt,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        var path = ValuePath(wraith, scope, name, runId, haunt);
        await using var lease = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        var current = File.Exists(path)
            ? await AtomicJsonFile.ReadAsync<DurableValueRecord>(
                path, cancellationToken).ConfigureAwait(false)
            : null;
        EnsureExpectedVersion(name, expectedVersion, current?.Version ?? 0);
        if (current is null)
        {
            return null;
        }

        File.Delete(path);
        return current with { Value = current.Value.Clone() };
    }

    private async Task<FileStream> AcquireLockAsync(
        string valuePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_lockPath);
        SensitiveFilePermissions.RestrictDirectory(_lockPath);
        var lockName = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(valuePath))));
        var path = Path.Combine(_lockPath, lockName + ".lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                SensitiveFilePermissions.RestrictFile(path);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string ValuePath(
        CanonicalName wraith,
        DurableValueScope scope,
        string name,
        string? runId,
        CanonicalName? haunt) =>
        Path.Combine(ScopePath(wraith, scope, runId, haunt), EncodeName(name) + ".json");

    private string ScopePath(
        CanonicalName wraith,
        DurableValueScope scope,
        string? runId,
        CanonicalName? haunt) => scope switch
        {
            DurableValueScope.Agent => Path.Combine(
                EnsureWraith(wraith), "state", "values"),
            DurableValueScope.Run => Path.Combine(
                EnsureRun(wraith, runId), "state", "values"),
            DurableValueScope.Haunt => Path.Combine(
                EnsureHaunt(haunt), "state", "values"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

    private string EnsureWraith(CanonicalName wraith)
    {
        var path = Path.Combine(_rootPath, "agents", wraith.Value);
        if (!File.Exists(Path.Combine(path, "agent.json")))
        {
            throw new DeckStateException($"Wraith '{wraith}' does not exist.");
        }

        return path;
    }

    private string EnsureRun(CanonicalName wraith, string? runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateSegment(runId, nameof(runId));
        var path = Path.Combine(EnsureWraith(wraith), "runs", runId);
        if (!File.Exists(Path.Combine(path, "run.json")))
        {
            throw new DeckStateException($"Run '{runId}' does not exist for '{wraith}'.");
        }

        return path;
    }

    private string EnsureHaunt(CanonicalName? haunt)
    {
        if (haunt is null)
        {
            throw new DeckStateException("Haunt-scoped state requires a haunt.");
        }

        var path = Path.Combine(_rootPath, "haunts", haunt.Value.Value);
        if (!File.Exists(Path.Combine(path, "haunt.json")))
        {
            throw new DeckStateException($"Haunt '{haunt.Value}' does not exist.");
        }

        return path;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Any(char.IsControl))
        {
            throw new DeckStateException(
                "Durable value names must be at most 256 characters and contain no control characters.");
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (!StringComparer.Ordinal.Equals(value, Path.GetFileName(value)) ||
            value is "." or "..")
        {
            throw new ArgumentException("The value must be a safe path segment.", parameterName);
        }
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined)
        {
            throw new DeckStateException("Undefined is not a portable durable value.");
        }
    }

    private static void ValidateRecord(
        DurableValueRecord record,
        DurableValueScope scope,
        string name,
        string? runId,
        CanonicalName? haunt)
    {
        if (record.SchemaVersion != DurableValueRecord.CurrentSchemaVersion ||
            record.Scope != scope ||
            !StringComparer.Ordinal.Equals(record.Name, name) ||
            !StringComparer.Ordinal.Equals(record.RunId, runId) ||
            !StringComparer.Ordinal.Equals(record.Haunt, haunt?.Value) ||
            !StringComparer.Ordinal.Equals(record.ContentHash, CanonicalJson.Hash(record.Value)))
        {
            throw new DeckStateException($"Durable value '{name}' failed validation.");
        }
    }

    private static void EnsureExpectedVersion(
        string name,
        long? expectedVersion,
        long currentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion ?? 0);
        if (expectedVersion is { } expected && expected != currentVersion)
        {
            throw new DeckStateException(
                $"Durable value '{name}' version conflict: expected {expected}, found {currentVersion}.");
        }
    }

    private static string EncodeName(string name) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
