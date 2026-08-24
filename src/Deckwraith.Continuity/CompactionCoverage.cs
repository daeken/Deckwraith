using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;
using Deckwraith.Core.State;

namespace Deckwraith.Continuity;

public static class CompactionCoverage
{
    public static CompactionSelection? SelectOldestPrefix(
        IReadOnlyList<ArchiveRecord> records,
        IReadOnlyList<CompactionDocument> existing,
        double fraction,
        int minimumRecords)
    {
        if (fraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRecords);
        ValidateArchive(records);
        ValidateExisting(existing, records);
        var first = existing.Count == 0 ? 1 : checked(existing[^1].LastSequence + 1);
        var uncompacted = records.Where(record => record.Sequence >= first).ToArray();
        if (uncompacted.Length < minimumRecords)
        {
            return null;
        }

        var boundaries = uncompacted.Where(record =>
            StringComparer.Ordinal.Equals(record.Kind, "context.committed")).ToArray();
        if (boundaries.Length == 0)
        {
            return null;
        }

        var targetCount = Math.Max(
            minimumRecords,
            (int)Math.Ceiling(uncompacted.Length * fraction));
        var targetSequence = checked(first + targetCount - 1);
        var boundary = boundaries.FirstOrDefault(record => record.Sequence >= targetSequence) ??
            boundaries[^1];
        var covered = records.Where(record =>
            record.Sequence >= first && record.Sequence <= boundary.Sequence).ToArray();
        if (covered.Length < minimumRecords)
        {
            return null;
        }

        return new CompactionSelection(
            first,
            boundary.Sequence,
            covered.Select(record => record.ContentHash).ToArray());
    }

    public static void ValidateExisting(
        IReadOnlyList<CompactionDocument> compactions,
        IReadOnlyList<ArchiveRecord> records)
    {
        ValidateArchive(records);
        long expectedFirst = 1;
        string? previousId = null;
        foreach (var compaction in compactions.OrderBy(
            compaction => compaction.FirstSequence))
        {
            if (!compaction.IsValid ||
                compaction.FirstSequence != expectedFirst ||
                !StringComparer.Ordinal.Equals(compaction.PreviousCompactionId, previousId))
            {
                throw new DeckStateException(
                    $"Compaction '{compaction.CompactionId}' breaks contiguous coverage.");
            }

            var source = records.Where(record =>
                record.Sequence >= compaction.FirstSequence &&
                record.Sequence <= compaction.LastSequence).ToArray();
            if (source.Length != compaction.SourceContentHashes.Count ||
                !source.Select(record => record.ContentHash)
                    .SequenceEqual(compaction.SourceContentHashes, StringComparer.Ordinal))
            {
                throw new DeckStateException(
                    $"Compaction '{compaction.CompactionId}' source hashes no longer match the archive.");
            }

            expectedFirst = checked(compaction.LastSequence + 1);
            previousId = compaction.CompactionId;
        }
    }

    private static void ValidateArchive(IReadOnlyList<ArchiveRecord> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Sequence != index + 1)
            {
                throw new DeckStateException("Archive records are not a contiguous sequence from one.");
            }
        }
    }
}
