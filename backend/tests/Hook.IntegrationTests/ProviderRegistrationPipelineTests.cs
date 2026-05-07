using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task RegisteredProvider_NewMessage_HeartbeatsAndAcknowledges()
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

        // A re-ping while listed extends the TTL AND sends an explicit ack so the
        // user knows their listing is still active and what they can do next —
        // silent heartbeats leave them wondering whether the system is broken.
        (await client.InjectTextAsync(phone, "I offer carpentry")).EnsureSuccessStatusCode();

        var ack = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("listed as a provider for", StringComparison.OrdinalIgnoreCase),
            since: listed.At,
            timeout: TimeSpan.FromSeconds(15));
        ack.Body.ShouldContain("carpentry", Case.Insensitive);
        ack.Body.ShouldContain("LEAVE", Case.Insensitive);
    }

    [Fact]
    public async Task Registration_LeaveAfterListed_RemovesProviderAndAllowsReListing()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000099";

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
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

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            (await providers.GetAsync(phone, default)).ShouldNotBeNull();
        }

        (await client.InjectTextAsync(phone, "LEAVE")).EnsureSuccessStatusCode();
        var unlistAck = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("You are unlisted", StringComparison.OrdinalIgnoreCase),
            since: listed.At,
            timeout: TimeSpan.FromSeconds(15));
        unlistAck.Body.ShouldContain("I offer", Case.Insensitive);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            (await providers.GetAsync(phone, default)).ShouldBeNull();
        }

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: unlistAck.At,
            timeout: TimeSpan.FromSeconds(15));
        detected.Body.ShouldContain("plumbing");
    }

    [Fact]
    public async Task Registration_LeaveMidDraft_AbandonsDraftBeforeListing()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000098";

        (await client.InjectTextAsync(phone, "I offer plumbing")).EnsureSuccessStatusCode();
        var detected = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "LEAVE")).EnsureSuccessStatusCode();
        var ended = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Session ended", StringComparison.OrdinalIgnoreCase),
            since: detected.At,
            timeout: TimeSpan.FromSeconds(15));
        ended.ShouldNotBeNull();

        using var scope = _fx.Factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldBeNull();
    }

    [Fact]
    public async Task ListedProvider_Greeting_GetsAcknowledgementWithServicesAndLeaveOption()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000097";

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

        (await client.InjectTextAsync(phone, "hi")).EnsureSuccessStatusCode();

        var ack = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("currently listed", StringComparison.OrdinalIgnoreCase),
            since: listed.At,
            timeout: TimeSpan.FromSeconds(15));

        ack.Body.ShouldContain("carpentry", Case.Insensitive);
        ack.Body.ShouldContain("LEAVE", Case.Insensitive);
        ack.Body.ShouldNotContain("YES or NO", Case.Insensitive);
        ack.Body.ShouldNotContain("REQUEST or REGISTER", Case.Insensitive);
    }
}
