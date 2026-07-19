using System.Collections.Concurrent;
using System.Threading.Channels;

namespace IsleBridge.Api.Streaming;

/// <summary>
/// Fans one out-stream (chat / events / stats / results) to any number of live SSE
/// subscribers. Lines are the raw NDJSON strings read off disk — the Api relays them
/// verbatim; the SDK does the typing. Each subscriber gets its own bounded channel so
/// a slow consumer drops its own oldest lines rather than blocking the pump or peers.
/// </summary>
public sealed class StreamHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    /// <summary>Number of currently attached subscribers (drives the stats flood guard).</summary>
    public int SubscriberCount => _subscribers.Count;

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    public void Publish(string line)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(line);
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public sealed class Subscription(StreamHub hub, Guid id, ChannelReader<string> reader) : IDisposable
    {
        public ChannelReader<string> Reader => reader;
        public void Dispose() => hub.Unsubscribe(id);
    }
}
