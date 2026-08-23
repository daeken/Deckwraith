using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Json;

namespace Deckwraith.Persistence.Archives;

public sealed class JsonlAgentArchive : IAgentArchive
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _rootPath;
    private readonly long _maximumSegmentBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentGates = new(StringComparer.Ordinal);

    public JsonlAgentArchive(string rootPath, long maximumSegmentBytes = 64L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentBytes);
        _rootPath = Path.GetFullPath(rootPath);
        _maximumSegmentBytes = maximumSegmentBytes;
    }

    public async Task<ArchiveRecord> AppendAsync(
        ArchiveEvent archiveEvent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveEvent.Agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveEvent.Kind);
        var wraith = CanonicalName.Parse(archiveEvent.Agent);
        var gate = _agentGates.GetOrAdd(wraith.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAllInternalAsync(wraith, cancellationToken).ConfigureAwait(false);
            var sequence = existing.Count == 0 ? 1 : checked(existing[^1].Sequence + 1);
            var previousHash = existing.Count == 0 ? null : existing[^1].ContentHash;
            var timestamp = archiveEvent.Timestamp ?? DateTimeOffset.UtcNow;
            var eventId = archiveEvent.EventId ?? Guid.CreateVersion7(timestamp).ToString("N");
            var contentHash = ComputeContentHash(
                ArchiveRecord.CurrentSchemaVersion,
                eventId,
                wraith.Value,
                archiveEvent.Haunt,
                archiveEvent.RunId,
                archiveEvent.ShellId,
                sequence,
                timestamp,
                archiveEvent.Kind,
                archiveEvent.Payload,
                previousHash);
            var record = new ArchiveRecord(
                ArchiveRecord.CurrentSchemaVersion,
                eventId,
                wraith.Value,
                archiveEvent.Haunt,
                archiveEvent.RunId,
                archiveEvent.ShellId,
                sequence,
                timestamp,
                archiveEvent.Kind,
                archiveEvent.Payload.Clone(),
                previousHash,
                contentHash);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, DeckJson.CompactOptions);
            var line = new byte[bytes.Length + 1];
            bytes.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            var segmentPath = SelectSegmentPath(wraith, line.Length);
            await using var stream = new FileStream(
                segmentPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            SensitiveFilePermissions.RestrictFile(segmentPath);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ArchiveRecord>> ReadAllAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken)
    {
        var gate = _agentGates.GetOrAdd(wraith.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllInternalAsync(wraith, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ArchiveRecord>> ReadAllInternalAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken)
    {
        var archivePath = ArchivePath(wraith);
        if (!Directory.Exists(archivePath))
        {
            throw new DeckStateException($"The archive for '{wraith}' does not exist.");
        }

        var records = new List<ArchiveRecord>();
        string? previousHash = null;
        long expectedSequence = 1;
        foreach (var segment in Directory.EnumerateFiles(archivePath, "*.jsonl").Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(segment, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                continue;
            }

            if (bytes[^1] != (byte)'\n')
            {
                throw new DeckStateException($"Archive segment '{segment}' has an incomplete trailing write.");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new DeckStateException($"Archive segment '{segment}' is not valid UTF-8.", exception);
            }

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ArchiveRecord record;
                try
                {
                    record = JsonSerializer.Deserialize<ArchiveRecord>(line, DeckJson.Options)
                        ?? throw new JsonException("Archive record was null.");
                }
                catch (JsonException exception)
                {
                    throw new DeckStateException($"Archive segment '{segment}' contains invalid JSON.", exception);
                }

                ValidateRecord(record, expectedSequence, previousHash);
                records.Add(record);
                previousHash = record.ContentHash;
                expectedSequence++;
            }
        }

        return records;
    }

    private string SelectSegmentPath(CanonicalName wraith, int appendedBytes)
    {
        var archivePath = ArchivePath(wraith);
        Directory.CreateDirectory(archivePath);
        SensitiveFilePermissions.RestrictDirectory(archivePath);
        var segments = Directory.EnumerateFiles(archivePath, "*.jsonl").Order(StringComparer.Ordinal).ToArray();
        if (segments.Length == 0)
        {
            return Path.Combine(archivePath, "000001.jsonl");
        }

        var last = segments[^1];
        var lastLength = new FileInfo(last).Length;
        if (lastLength == 0 || lastLength + appendedBytes <= _maximumSegmentBytes)
        {
            return last;
        }

        var number = int.Parse(Path.GetFileNameWithoutExtension(last), System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(archivePath, $"{checked(number + 1):D6}.jsonl");
    }

    private string ArchivePath(CanonicalName wraith) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "archive");

    private static void ValidateRecord(ArchiveRecord record, long expectedSequence, string? previousHash)
    {
        if (record.SchemaVersion != ArchiveRecord.CurrentSchemaVersion)
        {
            throw new DeckStateException($"Archive schema version {record.SchemaVersion} is unsupported.");
        }

        if (record.Sequence != expectedSequence)
        {
            throw new DeckStateException(
                $"Archive sequence {record.Sequence} was found where {expectedSequence} was expected.");
        }

        if (!StringComparer.Ordinal.Equals(record.PreviousContentHash, previousHash))
        {
            throw new DeckStateException($"Archive record {record.Sequence} has a broken hash chain.");
        }

        var computed = ComputeContentHash(
            record.SchemaVersion,
            record.EventId,
            record.Agent,
            record.Haunt,
            record.RunId,
            record.ShellId,
            record.Sequence,
            record.Timestamp,
            record.Kind,
            record.Payload,
            record.PreviousContentHash);
        if (!StringComparer.Ordinal.Equals(record.ContentHash, computed))
        {
            throw new DeckStateException($"Archive record {record.Sequence} has an invalid content hash.");
        }
    }

    private static string ComputeContentHash(
        int schemaVersion,
        string eventId,
        string agent,
        string? haunt,
        string? runId,
        string? shellId,
        long sequence,
        DateTimeOffset timestamp,
        string kind,
        JsonElement payload,
        string? previousContentHash)
    {
        var unsigned = new
        {
            SchemaVersion = schemaVersion,
            EventId = eventId,
            Agent = agent,
            Haunt = haunt,
            RunId = runId,
            ShellId = shellId,
            Sequence = sequence,
            Timestamp = timestamp,
            Kind = kind,
            Payload = payload,
            PreviousContentHash = previousContentHash,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, DeckJson.CompactOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }
}
