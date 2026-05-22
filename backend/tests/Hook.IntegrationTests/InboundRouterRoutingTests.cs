using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-2")]
public class InboundRouterRoutingTests : PipelineTestBase
{
    public InboundRouterRoutingTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Provider_CanAlsoCreateClientRequest_NotBlockedByAvailability()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002001";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var registered = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("listed for", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");

        var clientReply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("plumbing", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase),
            since: registered.At);

        clientReply.Body.ShouldContain("plumbing");
    }

    [Fact]
    public async Task BareWord_Request_RoutesToClientFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002003";

        await _fx.InjectTextAndAwaitAsync(phone, "request");
        var reply = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("service", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldContain("service");
    }

    [Fact]
    public async Task BareWord_Register_RoutesToProviderFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002004";

        await _fx.InjectTextAndAwaitAsync(phone, "register");
        var reply = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("offer", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldContain("offer");
    }

    [Fact]
    public async Task PickRegex_BypassesFunnel_WhenActiveRequestPresent()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002002";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "kitchen sink leak");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var presented = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "#1", timeout: TimeSpan.FromSeconds(15));

        var share = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #1: provider for ", StringComparison.Ordinal),
            since: presented.At);

        share.Body.ShouldContain("plumbing");
    }
}
