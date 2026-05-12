using System.Net;
using System.Threading.RateLimiting;
using Hook.Features.RateLimiting;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Hook.UnitTests.RateLimiting;

public class GlobalRateLimitPartitionerTests
{
    private static readonly IReadOnlySet<string> BypassHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "seq.hook.drop.africa" };

    private static readonly Func<HttpContext, RateLimitPartition<string>> Partition =
        GlobalRateLimitPartitioner.Build(new RateLimitOptions(), BypassHosts);

    private static HttpContext CtxFor(string path, string? host = null, string? token = null, IPAddress? ip = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (host is not null) ctx.Request.Host = new HostString(host);
        if (token is not null) ctx.Request.QueryString = new QueryString("?token=" + Uri.EscapeDataString(token));
        ctx.Connection.RemoteIpAddress = ip;
        return ctx;
    }

    [Fact]
    public void WebhookPath_Bypassed()
    {
        var part = Partition(CtxFor("/webhooks/whatsapp", host: "hook.drop.africa"));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void ChatHubPath_Bypassed()
    {
        var part = Partition(CtxFor("/hubs/chat/negotiate", host: "hook.drop.africa"));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void SeqHost_Bypassed()
    {
        var part = Partition(CtxFor("/", host: "seq.hook.drop.africa"));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void SeqHost_Match_IsCaseInsensitive()
    {
        var part = Partition(CtxFor("/", host: "Seq.Hook.Drop.Africa"));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void SeqHost_TrailingDot_StillBypassed()
    {
        var part = Partition(CtxFor("/", host: "seq.hook.drop.africa."));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void SpoofedSeqHostOnApiPath_StillBypasses()
    {
        // Behavior pin: host match does not require a path prefix — a spoofed Host
        // header for a non-bypass path is accepted because Caddy is the only network
        // ingress in prod. If you ever expose the API to a different origin you must
        // front it with a Host filter.
        var part = Partition(CtxFor("/api/anything", host: "seq.hook.drop.africa"));
        part.PartitionKey.ShouldBe(GlobalRateLimitPartitioner.BypassPartitionKey);
    }

    [Fact]
    public void FallbackToIp_WhenNoToken()
    {
        var part = Partition(CtxFor("/", host: "hook.drop.africa", ip: IPAddress.Parse("203.0.113.7")));
        part.PartitionKey.ShouldBe("ip:203.0.113.7");
    }

    [Fact]
    public void TokenPartitionKey_UsesRawToken()
    {
        var part = Partition(CtxFor("/c/abc", host: "hook.drop.africa", token: "super-secret-token"));
        part.PartitionKey.ShouldBe("t:super-secret-token");
    }

    [Fact]
    public void NullRemoteIp_FallsBackToUnknown()
    {
        var part = Partition(CtxFor("/", host: "hook.drop.africa", ip: null));
        part.PartitionKey.ShouldBe("ip:unknown");
    }

    [Fact]
    public void WhitespaceToken_FallsBackToIp()
    {
        var part = Partition(CtxFor("/", host: "hook.drop.africa", token: "   ", ip: IPAddress.Parse("203.0.113.9")));
        part.PartitionKey.ShouldBe("ip:203.0.113.9");
    }

    [Fact]
    public void OversizedToken_FallsBackToIp()
    {
        var huge = new string('a', GlobalRateLimitPartitioner.MaxTokenLength + 1);
        var part = Partition(CtxFor("/", host: "hook.drop.africa", token: huge, ip: IPAddress.Parse("203.0.113.4")));
        part.PartitionKey.ShouldBe("ip:203.0.113.4");
    }

    [Fact]
    public void BuildPartitionKey_IsStable()
    {
        var key1 = GlobalRateLimitPartitioner.BuildPartitionKey("same-token", null);
        var key2 = GlobalRateLimitPartitioner.BuildPartitionKey("same-token", null);
        var key3 = GlobalRateLimitPartitioner.BuildPartitionKey("different-token", null);

        key1.ShouldBe(key2);
        key1.ShouldNotBe(key3);
    }
}
