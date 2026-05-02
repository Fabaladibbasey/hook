using Shouldly;

namespace Hook.IntegrationTests;

public class ProviderRegistrationPipelineTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public ProviderRegistrationPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Registration_FullHappyPath_GpsLocation_ListsProvider()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000001";

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        detected.Body.ShouldContain("plumbing");

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var listed = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));
        listed.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Registration_EditServices_ReprompstForCorrectedList()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000002";

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "edit")).EnsureSuccessStatusCode();
        var corrected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("corrected list", StringComparison.OrdinalIgnoreCase),
            since: detected.At);
        corrected.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RegisteredProvider_NewMessage_HeartbeatsExistingListing()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000003";

        (await client.InjectTextAsync(phone, "I offer carpentry")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("Share your phone", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        var listed = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "still here")).EnsureSuccessStatusCode();

        await Task.Delay(800);
        var outbox = await client.GetOutboxAsync();
        var afterHeartbeat = outbox.Where(m => m.To == phone && m.At > listed.At).ToList();
        afterHeartbeat.ShouldBeEmpty();
    }
}
