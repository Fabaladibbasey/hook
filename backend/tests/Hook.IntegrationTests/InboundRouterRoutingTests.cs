using Shouldly;

namespace Hook.IntegrationTests;

public class InboundRouterRoutingTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public InboundRouterRoutingTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Provider_CanAlsoCreateClientRequest_NotBlockedByAvailability()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+14155552001";

        (await client.InjectTextAsync(phone, "I offer carpentry"))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location", StringComparison.OrdinalIgnoreCase));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var registered = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("listed for", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "I need a plumber"))
            .EnsureSuccessStatusCode();

        var clientReply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("plumbing", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase),
            since: registered.At,
            timeout: TimeSpan.FromSeconds(10));

        clientReply.Body.ShouldContain("plumbing");
    }

    [Fact]
    public async Task PickRegex_BypassesFunnel_WhenActiveRequestPresent()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+14155552002";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));
        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));
        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));
        (await client.InjectTextAsync(phone, "kitchen sink leak")).EnsureSuccessStatusCode();

        var presented = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15));

        (await client.InjectTextAsync(phone, "#1")).EnsureSuccessStatusCode();

        var share = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        share.Body.ShouldContain("plumbing");
    }
}
