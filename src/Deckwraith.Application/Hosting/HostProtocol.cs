using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Deckwraith.Application.Hosting;

public static class HostProtocol
{
    public const int CurrentVersion = 1;

    public static void ValidateVersion(int version)
    {
        if (version != CurrentVersion)
        {
            throw new HostProtocolException(
                "unsupported-protocol",
                $"Host protocol {version} is not supported; expected {CurrentVersion}.");
        }
    }
}

public enum HostRequestKind
{
    Command,
    Query,
}

public sealed record HostRequest(
    int ProtocolVersion,
    string RequestId,
    HostRequestKind Kind,
    string Name,
    JsonElement Payload);

public sealed record HostProtocolError(
    string Code,
    string Message,
    bool Retryable);

public sealed record HostResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    JsonElement? Result,
    HostProtocolError? Error,
    long EventCursor)
{
    public static HostResponse Completed(
        HostRequest request,
        object? result,
        long eventCursor) =>
        new(
            HostProtocol.CurrentVersion,
            request.RequestId,
            true,
            result is null ? null : JsonSerializer.SerializeToElement(result),
            null,
            eventCursor);

    public static HostResponse Failed(
        HostRequest request,
        HostProtocolError error,
        long eventCursor) =>
        new(
            HostProtocol.CurrentVersion,
            request.RequestId,
            false,
            null,
            error,
            eventCursor);
}

public sealed record HostEvent(
    int ProtocolVersion,
    long Cursor,
    string Name,
    DateTimeOffset Timestamp,
    JsonElement Payload);

public sealed record HostSchemaDescriptor(
    int ProtocolVersion,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> Events);

public class HostProtocolException : Exception
{
    public HostProtocolException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed class HostEventGapException : HostProtocolException
{
    public HostEventGapException(long requestedCursor, long oldestCursor)
        : base(
            "event-gap",
            $"Event cursor {requestedCursor} is older than retained cursor {oldestCursor}; refresh snapshots before reconnecting.")
    {
        RequestedCursor = requestedCursor;
        OldestCursor = oldestCursor;
    }

    public long RequestedCursor { get; }

    public long OldestCursor { get; }
}

public sealed class HostEventBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<HostEvent> _events = new();
    private readonly Dictionary<Guid, Channel<HostEvent>> _subscribers = [];
    private long _nextCursor;
    private bool _disposed;

    public HostEventBuffer(int capacity = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public long LatestCursor
    {
        get
        {
            lock (_gate)
            {
                return _nextCursor;
            }
        }
    }

    public HostEvent Publish(string name, object payload, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var hostEvent = new HostEvent(
                HostProtocol.CurrentVersion,
                checked(++_nextCursor),
                name,
                timestamp,
                JsonSerializer.SerializeToElement(payload));
            _events.Enqueue(hostEvent);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
            }

            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryWrite(hostEvent);
            }

            return hostEvent;
        }
    }

    public async IAsyncEnumerable<HostEvent> ReadAsync(
        long afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterCursor);
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<HostEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_events.TryPeek(out var oldest) && afterCursor < oldest.Cursor - 1)
            {
                throw new HostEventGapException(afterCursor, oldest.Cursor);
            }

            foreach (var hostEvent in _events.Where(hostEvent => hostEvent.Cursor > afterCursor))
            {
                channel.Writer.TryWrite(hostEvent);
            }

            _subscribers.Add(subscriberId, channel);
        }

        try
        {
            await foreach (var hostEvent in channel.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return hostEvent;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriberId);
                channel.Writer.TryComplete();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
            _events.Clear();
        }
    }
}
