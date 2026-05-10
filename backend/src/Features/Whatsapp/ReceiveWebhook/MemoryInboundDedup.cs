using Microsoft.Extensions.Caching.Memory;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class MemoryInboundDedup(IMemoryCache cache) : IInboundDedup
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public bool TryAcquire(string messageId)
    {
        if (cache.TryGetValue(Key(messageId), out _)) return false;
        cache.Set(Key(messageId), true, Ttl);
        return true;
    }

    public void Reset()
    {
        if (cache is MemoryCache mc) mc.Compact(1.0);
    }

    private static string Key(string id) => $"wa:msg:{id}";
}
