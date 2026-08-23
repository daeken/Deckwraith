using System.Text;
using Deckwraith.Core.Naming;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.State;

namespace Deckwraith.Persistence.Tests;

public sealed class ContentAddressedArtifactStoreTests
{
    [Fact]
    public async Task PutDeduplicatesIdenticalContentBySha256()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var state = new JsonDeckStateStore(temporaryDirectory.Path);
        await state.InitializeAsync(
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        await state.CreateHauntAsync(
            CanonicalName.Parse("deckwraith"),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        var store = new ContentAddressedArtifactStore(temporaryDirectory.Path);
        var bytes = Encoding.UTF8.GetBytes("the same immutable artifact");

        var first = await store.PutAsync(
            CanonicalName.Parse("deckwraith"),
            new MemoryStream(bytes),
            "text/plain",
            CancellationToken.None);
        var second = await store.PutAsync(
            CanonicalName.Parse("deckwraith"),
            new MemoryStream(bytes),
            "text/plain",
            CancellationToken.None);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(bytes.Length, first.Length);
        await using var stored = await store.OpenReadAsync(
            CanonicalName.Parse("deckwraith"), first.Hash, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, CancellationToken.None);
        Assert.Equal(bytes, buffer.ToArray());
    }
}
