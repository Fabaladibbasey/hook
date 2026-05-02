using Hook.Features.Whatsapp.ReceiveWebhook;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Hook.UnitTests.Whatsapp;

public class MemoryInboundDedupTests
{
    [Fact]
    public void TryAcquire_ShouldReturnTrueOnFirstCallAndFalseOnRepeat()
    {
        var dedup = new MemoryInboundDedup(new MemoryCache(new MemoryCacheOptions()));

        dedup.TryAcquire("wamid.1").ShouldBeTrue();
        dedup.TryAcquire("wamid.1").ShouldBeFalse();
        dedup.TryAcquire("wamid.2").ShouldBeTrue();
    }
}
