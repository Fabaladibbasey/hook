using Hook.Features.ServiceRequest.Create;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

public class ClientRequestPipelineTests : IClassFixture<DevPipelineFixture>
{
    private readonly DevPipelineFixture _fx;

    public ClientRequestPipelineTests(DevPipelineFixture fx) => _fx = fx;

    [Fact]
    public async Task Greeting_GetsGreetingBack_NotServicePitch()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551001";

        (await client.InjectTextAsync(phone, "hi")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("greeting-reply", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        reply.Body.ShouldNotBeNullOrEmpty();
        reply.Body.ShouldNotContain("YES or NO", Case.Insensitive);
        reply.Body.ShouldNotContain("REQUEST or REGISTER", Case.Insensitive);
    }

    [Fact]
    public async Task OutOfScope_GetsRefusal_NotOpenChat()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551009";

        (await client.InjectTextAsync(phone, "what's the weather today")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("out-of-scope", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        reply.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_AsksToConfirmService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551002";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));
        reply.Body.ShouldContain("plumbing");
    }

    // Regression for the screenshot bug: a problem statement like "My door is broken"
    // must route to the ClientRequestOrchestrator (which says "Do you need X? Reply
    // YES or NO"), NOT to the RegistrationOrchestrator (which says "I detected: ...
    // Reply YES to confirm or EDIT to change").
    [Fact]
    public async Task ProblemStatement_DoorIsBroken_RoutesToClientRequest_NotProviderRegistration()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551011";

        (await client.InjectTextAsync(phone, "My door is broken")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(8));

        reply.Body.ShouldContain("Do you need", Case.Insensitive);
        reply.Body.ShouldContain("carpentry");
        reply.Body.ShouldNotContain("EDIT", Case.Sensitive);
        reply.Body.ShouldNotContain("I detected", Case.Insensitive);
    }

    [Fact]
    public async Task ServiceRequest_FullHappyPath_GpsLocation_ReachesMatching()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551003";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "kitchen sink leak")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();

        var lookingFor = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15));

        var presented = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            since: lookingFor.At,
            timeout: TimeSpan.FromSeconds(15));

        presented.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_PickProvider_SharesContactBothSides()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551006";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "kitchen sink leak")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();

        var presented = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15));

        const string topProviderPhone = "+2203000001";
        (await client.InjectTextAsync(phone, "PICK 1")).EnsureSuccessStatusCode();

        var clientShare = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(20));

        clientShare.Body.ShouldContain("plumbing");
        clientShare.Body.ShouldContain(topProviderPhone);

        var outbox = await client.GetOutboxAsync();
        // Filter on client phone too — the fixture's outbox is shared across all
        // tests, and any other parallel client funnel ending in the same provider
        // pool will leave a similarly-shaped "Client wants ..." notification.
        var providerNotice = outbox.FirstOrDefault(m =>
            m.To == topProviderPhone &&
            m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
            m.Body.Contains(phone, StringComparison.Ordinal));
        providerNotice.ShouldNotBeNull();
    }

    [Fact]
    public async Task ServiceRequest_FreeTextShareContact_AfterMatch_AsksWhichMatch_ThenShares()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551010";

        // Free-text "give me the contact details" implies the requester is OK with
        // sharing — drive intake with consent=true so the bilateral-consent path
        // can reveal phones.
        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone, sharePhoneConsent: true);

        (await client.InjectTextAsync(phone, "give me the contact details")).EnsureSuccessStatusCode();

        var ambiguity = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(15));

        ambiguity.Body.ShouldContain("Reply 1");

        const string topProviderPhone = "+2203000001";
        (await client.InjectTextAsync(phone, "1")).EnsureSuccessStatusCode();

        var clientShare = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase),
            since: ambiguity.At,
            timeout: TimeSpan.FromSeconds(15));

        clientShare.Body.ShouldContain("plumbing");
        clientShare.Body.ShouldContain(topProviderPhone);
    }

    [Fact]
    public async Task ServiceRequest_AddressText_GetsGeocodedAndConfirmed()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551004";

        (await client.InjectTextAsync(phone, "I need a carpenter")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectTextAsync(phone, "1 Market St San Francisco")).EnsureSuccessStatusCode();
        var found = await client.WaitForOutboundAsync(phone, m => m.Body.StartsWith("Found:"));
        found.Body.ShouldContain("Market St");
    }

    [Fact]
    public async Task PresentAsync_NotifiesNoProvider_UntilClientPicks()
    {
        // Privacy invariant: matched providers must learn nothing until the client
        // picks them. Earlier behaviour proactively fanned out the request to every
        // ShareContact-true provider — that leaked the client's number to providers
        // who were never selected.
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551007";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        // Give the bus a beat to fire any (hypothetical) deferred broadcast.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var outbox = await client.GetOutboxAsync();
        var providerNotices = outbox
            .Where(m => m.At >= presented.At
                        && m.To.StartsWith("+220300000", StringComparison.Ordinal)
                        && m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase)
                        && m.Body.Contains(phone, StringComparison.Ordinal))
            .ToList();
        providerNotices.ShouldBeEmpty();
    }

    [Fact]
    public async Task QuickIntent_LiteralNo_TakesNoAsAnswer_NoLoop()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551008";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        var prompt = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();
        var afterNo = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase),
            since: prompt.At,
            timeout: TimeSpan.FromSeconds(5));

        afterNo.Body.ShouldNotContain("Reply YES or NO", Case.Insensitive);
    }

    [Fact]
    public async Task SecondMessageMidFunnel_ReprompstYesNo_NotRestartFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+14155551005";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        var first = await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();

        var reprompt = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase),
            since: first.At);
        reprompt.Body.ShouldNotContain("Do you need", Case.Insensitive);
    }

    [Fact]
    public async Task EndsSession_ClientFunnel_DeletesDraftAndReplies()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000091";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));

        (await client.InjectTextAsync(phone, "bye")).EnsureSuccessStatusCode();
        var ended = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Session ended", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));
        ended.ShouldNotBeNull();

        using var scope = _fx.Factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IClientRequestDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldBeNull();
    }

    [Fact]
    public async Task FuzzyConfirmation_AdvancesClientFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000092";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));

        (await client.InjectTextAsync(phone, "of course")).EnsureSuccessStatusCode();
        var advanced = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Send your location", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));
        advanced.ShouldNotBeNull();
    }

    [Fact]
    public async Task ServiceRequest_DescriptionStep_AmbiguousReply_TreatedAsSkip()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000093";

        (await client.InjectTextAsync(phone, "I need a plumber")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone, m => m.Body.Contains("location pin"));

        (await client.InjectLocationAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng))
            .EnsureSuccessStatusCode();
        var descPrompt = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        // Ambiguous reply that is neither SKIP nor a real description.
        (await client.InjectTextAsync(phone, "I'm good")).EnsureSuccessStatusCode();
        await client.WaitForOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At);

        (await client.InjectTextAsync(phone, "no")).EnsureSuccessStatusCode();

        var lookingFor = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At,
            timeout: TimeSpan.FromSeconds(15));
        lookingFor.ShouldNotBeNull();
    }

    [Fact]
    public async Task ServiceRequest_YesAfterPresent_RepromptsWhenMultiple()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000094";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, "yes")).EnsureSuccessStatusCode();

        var ambiguity = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(15));

        ambiguity.Body.ShouldContain("Reply 1");
    }

    // Regression for the screenshot bug: AI presenter paraphrased the CTA into
    // "Would you like more information or want to proceed?". User replies
    // "Proceed" / "Detail" — these must route to ShareTopOrAskAsync (same as
    // "yes"), NOT fall through to the LLM where they'd mis-classify.
    [Theory]
    [InlineData("proceed", "+220700000201")]
    [InlineData("Proceed", "+220700000202")]
    [InlineData("PROCEED", "+220700000203")]
    [InlineData("Detail", "+220700000204")]
    [InlineData("details", "+220700000205")]
    [InlineData("more info", "+220700000206")]
    [InlineData("continue", "+220700000207")]
    [InlineData("connect", "+220700000208")]
    [InlineData("go ahead", "+220700000209")]
    public async Task ServiceRequest_NaturalConfirmAfterPresent_RoutesToShareTopOrAsk(string reply, string phone)
    {
        using var client = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, reply)).EnsureSuccessStatusCode();

        var followup = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.StartsWith("Provider for ", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(15));

        followup.Body.ShouldNotContain("listed as a provider", Case.Insensitive);
    }

    // "NEW" advertised in MatchPresenter / IterationCoordinator pickHint must
    // close the active request and prompt for a fresh service. Previously fell
    // through to the LLM with no deterministic handler.
    [Fact]
    public async Task ServiceRequest_NewAfterPresent_ClosesRequestAndPromptsFresh()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000096";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(client, phone);

        (await client.InjectTextAsync(phone, "NEW")).EnsureSuccessStatusCode();

        var freshPrompt = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("what service do you need", StringComparison.OrdinalIgnoreCase),
            since: presented.At,
            timeout: TimeSpan.FromSeconds(15));

        freshPrompt.Body.ShouldContain("I need", Case.Insensitive);
    }

    [Fact]
    public async Task ServiceRequest_DeliveryFromMarketplaceContext_ConfirmsDelivery()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000095";

        (await client.InjectTextAsync(
            phone,
            "I need to delivery what I bought from facebook marketplace"))
            .EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));

        reply.Body.ShouldContain("delivery", Case.Insensitive);
        reply.Body.ShouldNotContain("food", Case.Insensitive);
    }

    // Person-transport (taxi/cab/passenger) and parcel-delivery are different
    // services. A taxi request must NOT be matched against couriers.
    [Fact]
    public async Task ServiceRequest_TaxiRequest_RoutesToRideSlug_NotDelivery()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000301";

        (await client.InjectTextAsync(phone, "I need a taxi to airport")).EnsureSuccessStatusCode();

        var reply = await client.WaitForOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));

        reply.Body.ShouldContain("ride", Case.Insensitive);
        reply.Body.ShouldNotContain("delivery", Case.Insensitive);
    }
}
