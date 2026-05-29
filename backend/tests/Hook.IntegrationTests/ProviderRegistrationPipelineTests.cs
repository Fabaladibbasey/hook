using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-3")]
public class ProviderRegistrationPipelineTests : PipelineTestBase
{
    public ProviderRegistrationPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Registration_FullHappyPath_GpsLocation_ListsProvider()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000001";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));
        detected.Body.ShouldContain("plumbing");

        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));
        listed.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task MidFlow_QuestionAtAwaitingLocation_AnswersAndReprompts_UnlistedDraft()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070000051";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        var locationPrompt = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase));

        var drafts = _fx.Factory.Services.GetRequiredService<IRegistrationDraftRepository>();
        var updatedAtBefore = (await drafts.GetAsync(phone, default))!.UpdatedAt;

        await _fx.InjectTextAndAwaitAsync(phone, "is my data private?");

        var aiAnswer = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal),
            since: locationPrompt.At);
        var reprompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("location pin", StringComparison.OrdinalIgnoreCase),
            since: locationPrompt.At);

        aiAnswer.Body.ShouldContain("is my data private?");
        reprompt.Body.ShouldContain("location pin", Case.Insensitive);

        var draft = await drafts.GetAsync(phone, default);
        draft.ShouldNotBeNull();
        draft!.Step.ShouldBe(RegistrationStep.AwaitingLocation);
        // Mid-flow Q&A must not bump UpdatedAt — spam Q&A cannot block draft expiry.
        draft.UpdatedAt.ShouldBe(updatedAtBefore);
    }

    [Fact]
    public async Task MidFlow_QuestionFromListedProvider_TakesColdPath_DoesNotExtendTtl()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070000052";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        var availability = _fx.Factory.Services.GetRequiredService<IProviderAvailabilityRepository>();
        var beforeTtl = (await availability.GetAsync(phone, default))!.ExpiresAt;

        await _fx.InjectTextAndAwaitAsync(phone, "is my data safe?");

        // Cold-path dispatcher sends the ack BEFORE the AI answer.
        var ack = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("one sec", StringComparison.OrdinalIgnoreCase),
            since: listed.At);
        var aiAnswer = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal),
            since: ack.At);
        aiAnswer.Body.ShouldContain("is my data safe?");

        // Heartbeat-extension reply must NOT have been sent for the Q&A inbound.
        var outbox = await client.GetOutboxAsync();
        outbox
            .Where(m => m.To == phone && m.At > listed.At)
            .ShouldAllBe(m => !m.Body.Contains("extended for", StringComparison.OrdinalIgnoreCase));

        // TTL is identical — cold-path Q&A short-circuits BEFORE the orchestrator's
        // heartbeat branch, so the listed-provider TTL stays put.
        var afterTtl = (await availability.GetAsync(phone, default))!.ExpiresAt;
        afterTtl.ShouldBe(beforeTtl);
    }

    [Fact]
    public async Task Registration_EditServices_ReprompstForCorrectedList()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000002";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "edit");
        var corrected = await client.ExpectOutboundAsync(
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

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        // A re-ping while listed extends the TTL AND sends an explicit ack so the
        // user knows their listing is still active and what they can do next —
        // silent heartbeats leave them wondering whether the system is broken.
        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");

        var ack = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("listed as a provider for", StringComparison.OrdinalIgnoreCase),
            since: listed.At);
        ack.Body.ShouldContain("carpentry", Case.Insensitive);
        ack.Body.ShouldContain("LEAVE", Case.Insensitive);
    }

    [Fact]
    public async Task Registration_LeaveAfterListed_RemovesProviderAndAllowsReListing()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000099";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            (await providers.GetAsync(phone, default)).ShouldNotBeNull();
        }

        await _fx.InjectTextAndAwaitAsync(phone, "LEAVE");
        var unlistAck = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are unlisted", StringComparison.OrdinalIgnoreCase),
            since: listed.At);
        unlistAck.Body.ShouldContain("I offer", Case.Insensitive);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            (await providers.GetAsync(phone, default)).ShouldBeNull();
        }

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase),
            since: unlistAck.At);
        detected.Body.ShouldContain("plumbing");
    }

    [Fact]
    public async Task Registration_LeaveMidDraft_AbandonsDraftBeforeListing()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000098";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer plumbing");
        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "LEAVE");
        var ended = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Session ended", StringComparison.OrdinalIgnoreCase),
            since: detected.At);
        ended.ShouldNotBeNull();

        using var scope = _fx.Factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldBeNull();
    }

    // A provider offering passenger transport ("I offer taxi", "I do cab")
    // must be registered under the ride slug, not delivery — otherwise their
    // listing surfaces for parcel-courier requests.
    [Fact]
    public async Task Registration_TaxiOffer_RegistersUnderRideSlug_NotDelivery()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000401";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer taxi");

        var detected = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("I detected:", StringComparison.OrdinalIgnoreCase));

        detected.Body.ShouldContain("ride", Case.Insensitive);
        detected.Body.ShouldNotContain("delivery", Case.Insensitive);
    }

    [Fact]
    public async Task ListedProvider_Greeting_GetsAcknowledgementWithServicesAndLeaveOption()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+2207000097";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        var listed = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("You are listed", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "hi");

        var ack = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("currently listed", StringComparison.OrdinalIgnoreCase),
            since: listed.At);

        ack.Body.ShouldContain("carpentry", Case.Insensitive);
        ack.Body.ShouldContain("LEAVE", Case.Insensitive);
        ack.Body.ShouldNotContain("YES or NO", Case.Insensitive);
        ack.Body.ShouldNotContain("REQUEST or REGISTER", Case.Insensitive);
    }
}
