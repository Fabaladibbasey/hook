using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Hook.Features.Whatsapp.Dev;

public sealed record DevOutboxMessage(
    DateTimeOffset At,
    string To,
    string Body,
    string MessageId);

public interface IDevOutbox
{
    void Publish(DevOutboxMessage message);
    IReadOnlyList<DevOutboxMessage> Recent();
    IAsyncEnumerable<DevOutboxMessage> Subscribe(CancellationToken ct);
    void Reset();
}

public sealed class DevOutbox(IOptions<DevWhatsappOptions> options) : IDevOutbox
{
    private readonly int _ringSize = Math.Max(1, options.Value.OutboxRingSize);
    private readonly DevOutboxMessage?[] _ring = new DevOutboxMessage?[Math.Max(1, options.Value.OutboxRingSize)];
    private int _head; // next-write slot
    private int _count; // valid entries (≤ ringSize)
    private readonly Lock _recentLock = new();
    private readonly ConcurrentDictionary<Guid, Channel<DevOutboxMessage>> _subs = new();

    public void Publish(DevOutboxMessage message)
    {
        lock (_recentLock)
        {
            _ring[_head] = message;
            _head = (_head + 1) % _ringSize;
            if (_count < _ringSize) _count++;
        }

        foreach (var ch in _subs.Values)
            ch.Writer.TryWrite(message);
    }

    public IReadOnlyList<DevOutboxMessage> Recent()
    {
        lock (_recentLock)
        {
            if (_count == 0) return Array.Empty<DevOutboxMessage>();
            var snap = new DevOutboxMessage[_count];
            // Oldest is at (_head - _count) mod _ringSize.
            var start = (_head - _count + _ringSize) % _ringSize;
            for (var i = 0; i < _count; i++)
                snap[i] = _ring[(start + i) % _ringSize]!;
            return snap;
        }
    }

    public void Reset()
    {
        lock (_recentLock)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
        }
    }

    public async IAsyncEnumerable<DevOutboxMessage> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<DevOutboxMessage>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _subs[id] = channel;
        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
                yield return msg;
        }
        finally
        {
            _subs.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}
