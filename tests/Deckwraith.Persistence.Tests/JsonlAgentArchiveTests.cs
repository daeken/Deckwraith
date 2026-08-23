using System.Text;
using System.Text.Json;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Archives;

namespace Deckwraith.Persistence.Tests;

public sealed class JsonlAgentArchiveTests
{
    [Fact]
    public async Task AppendRotatesSegmentsAndPreservesAValidHashChain()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = System.IO.Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "archive");
        Directory.CreateDirectory(archivePath);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path, maximumSegmentBytes: 700);

        for (var index = 0; index < 4; index++)
        {
            await archive.AppendAsync(
                new ArchiveEvent(
                    "wraith1",
                    "test.recorded",
                    JsonSerializer.SerializeToElement(new { index, text = new string('x', 80) })),
                CancellationToken.None);
        }

        var records = await archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None);
        Assert.Equal([1L, 2L, 3L, 4L], records.Select(record => record.Sequence));
        Assert.Null(records[0].PreviousContentHash);
        Assert.Equal(records[2].ContentHash, records[3].PreviousContentHash);
        Assert.True(Directory.EnumerateFiles(archivePath, "*.jsonl").Count() > 1);
    }

    [Fact]
    public async Task ReadRejectsAnIncompleteTrailingWrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = System.IO.Path.Combine(
            temporaryDirectory.Path, "agents", "wraith1", "archive");
        Directory.CreateDirectory(archivePath);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(archivePath, "000001.jsonl"),
            "{\"schemaVersion\":1}",
            Encoding.UTF8,
            CancellationToken.None);
        var archive = new JsonlAgentArchive(temporaryDirectory.Path);

        var exception = await Assert.ThrowsAsync<DeckStateException>(() => archive.ReadAllAsync(
            CanonicalName.Parse("wraith1"), CancellationToken.None));

        Assert.Contains("incomplete trailing write", exception.Message, StringComparison.Ordinal);
    }
}
