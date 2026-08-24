using System.Text.Json;
using Deckwraith.Continuity;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Context;

namespace Deckwraith.Continuity.Tests;

public sealed class CompactionCoverageTests
{
    [Fact]
    public void SelectionAlwaysStartsAtFrontierAndEndsOnACompleteBoundary()
    {
        var random = new Random(0xD3C);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var count = random.Next(8, 160);
            var boundaryStride = random.Next(2, 9);
            var records = Enumerable.Range(1, count)
                .Select(sequence => Record(
                    sequence,
                    sequence % boundaryStride == 0 ? "context.committed" : "message.user"))
                .ToArray();
            var selection = CompactionCoverage.SelectOldestPrefix(
                records, [], random.NextDouble() * 0.8 + 0.1, 3);
            if (selection is null)
            {
                Assert.DoesNotContain(records, record => record.Kind == "context.committed");
                continue;
            }

            Assert.Equal(1, selection.FirstSequence);
            Assert.Equal(
                "context.committed",
                records[(int)selection.LastSequence - 1].Kind);
            Assert.Equal(
                Enumerable.Range(0, (int)selection.LastSequence)
                    .Select(index => records[index].ContentHash),
                selection.SourceContentHashes);

            var firstCompaction = Document(selection, null, "compaction-1");
            var next = CompactionCoverage.SelectOldestPrefix(
                records, [firstCompaction], 0.5, 1);
            if (next is not null)
            {
                Assert.Equal(selection.LastSequence + 1, next.FirstSequence);
                Assert.True(next.LastSequence >= next.FirstSequence);
                Assert.Equal(
                    "context.committed",
                    records[(int)next.LastSequence - 1].Kind);
            }
        }
    }

    [Fact]
    public void GapsOverlapsAndChangedSourceHashesAreRejected()
    {
        var records = Enumerable.Range(1, 12)
            .Select(sequence => Record(sequence, sequence % 3 == 0
                ? "context.committed"
                : "message.user"))
            .ToArray();
        var first = new CompactionSelection(
            1, 6, records[..6].Select(record => record.ContentHash).ToArray());
        var firstDocument = Document(first, null, "compaction-1");
        var gapped = Document(
            new CompactionSelection(
                8, 9, records[7..9].Select(record => record.ContentHash).ToArray()),
            "compaction-1",
            "compaction-2");

        Assert.Throws<Deckwraith.Core.State.DeckStateException>(() =>
            CompactionCoverage.ValidateExisting([firstDocument, gapped], records));
        var changed = firstDocument with
        {
            SourceContentHashes =
            [
                "sha256:changed",
                .. firstDocument.SourceContentHashes.Skip(1),
            ],
        };
        Assert.Throws<Deckwraith.Core.State.DeckStateException>(() =>
            CompactionCoverage.ValidateExisting([changed], records));
    }

    private static ArchiveRecord Record(int sequence, string kind) =>
        new(
            ArchiveRecord.CurrentSchemaVersion,
            $"event-{sequence}",
            "wraith1",
            null,
            null,
            null,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            kind,
            JsonSerializer.SerializeToElement(new { sequence }),
            sequence == 1 ? null : $"sha256:{sequence - 1}",
            $"sha256:{sequence}");

    private static CompactionDocument Document(
        CompactionSelection selection,
        string? previous,
        string id) =>
        new(
            CompactionDocument.CurrentSchemaVersion,
            id,
            "wraith1",
            selection.FirstSequence,
            selection.LastSequence,
            selection.SourceContentHashes,
            previous,
            "fake",
            "model",
            "v1",
            JsonSerializer.SerializeToElement(new { }),
            "summary",
            [],
            [],
            DateTimeOffset.UnixEpoch,
            true,
            null,
            null);
}
