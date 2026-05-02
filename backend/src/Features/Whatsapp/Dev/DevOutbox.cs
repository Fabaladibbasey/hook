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
}

public sealed class DevOutbox(IOptions<DevWhatsappOptions> options) : IDevOutbox
{
    private readonly int _ringSize = Math.Max(1, options.Value.OutboxRingSize);
    private readonly LinkedList<DevOutboxMessage> _recent = new();
    private readonly Lock _recentLock = new();
    private readonly ConcurrentDictionary<Guid, Channel<DevOutboxMessage>> _subs = new();

    public void Publish(DevOutboxMessage message)
    {
        lock (_recentLock)
        {
            _recent.AddLast(message);
            while (_recent.Count > _ringSize)
                _recent.RemoveFirst();
        }

        foreach (var ch in _subs.Values)
            ch.Writer.TryWrite(message);
    }

    public IReadOnlyList<DevOutboxMessage> Recent()
    {
        lock (_recentLock)
            return [.. _recent];
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
