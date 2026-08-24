using Deckwraith.Application.Hosting;

namespace Deckwraith.IntegrationTests;

public sealed class HostProtocolTests
{
    [Fact]
    public async Task ReconnectReplaysBufferedEventsThenContinuesLive()
    {
        using var events = new HostEventBuffer(capacity: 4);
        events.Publish("run.started", new { runId = "run-1" }, DateTimeOffset.UnixEpoch);
        events.Publish("model.delta", new { text = "hel" }, DateTimeOffset.UnixEpoch);

        await using var reader = events.ReadAsync(1).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2, reader.Current.Cursor);
        Assert.Equal("model.delta", reader.Current.Name);

        events.Publish("model.delta", new { text = "lo" }, DateTimeOffset.UnixEpoch);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, reader.Current.Cursor);
        Assert.Equal("lo", reader.Current.Payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReconnectGapRequiresSnapshotRefresh()
    {
        using var events = new HostEventBuffer(capacity: 2);
        events.Publish("one", new { }, DateTimeOffset.UnixEpoch);
        events.Publish("two", new { }, DateTimeOffset.UnixEpoch);
        events.Publish("three", new { }, DateTimeOffset.UnixEpoch);

        await using var reader = events.ReadAsync(0).GetAsyncEnumerator();
        var exception = await Assert.ThrowsAsync<HostEventGapException>(async () =>
            await reader.MoveNextAsync());

        Assert.Equal(0, exception.RequestedCursor);
        Assert.Equal(2, exception.OldestCursor);
    }

    [Fact]
    public void VersionMismatchIsRejectedBeforeDispatch()
    {
        var exception = Assert.Throws<HostProtocolException>(() =>
            HostProtocol.ValidateVersion(HostProtocol.CurrentVersion + 1));

        Assert.Equal("unsupported-protocol", exception.Code);
    }
}
