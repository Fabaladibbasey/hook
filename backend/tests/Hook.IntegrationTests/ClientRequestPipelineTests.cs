using Hook.Features.ServiceRequest.Create;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests;

[Collection("Pipeline-1")]
public class ClientRequestPipelineTests : PipelineTestBase
{
    public ClientRequestPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Greeting_GetsGreetingBack_NotServicePitch()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001001";

        await _fx.InjectTextAndAwaitAsync(phone, "hi");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("greeting-reply", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldNotBeNullOrEmpty();
        reply.Body.ShouldNotContain("YES or NO", Case.Insensitive);
        reply.Body.ShouldNotContain("REQUEST or REGISTER", Case.Insensitive);
    }

    [Fact]
    public async Task OutOfScope_GetsRefusal_NotOpenChat()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001009";

        await _fx.InjectTextAndAwaitAsync(phone, "what's the weather today");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("out-of-scope", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_AsksToConfirmService()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001002";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");

        var reply = await client.ExpectOutboundAsync(phone, m => m.Body.Contains("YES or NO"));
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
        var phone = "+22070001011";

        await _fx.InjectTextAndAwaitAsync(phone, "My door is broken");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldContain("Do you need", Case.Insensitive);
        reply.Body.ShouldContain("carpentry");
        reply.Body.ShouldNotContain("EDIT", Case.Sensitive);
        reply.Body.ShouldNotContain("I detected", Case.Insensitive);
    }

    [Fact]
    public async Task ServiceRequest_FullHappyPath_GpsLocation_ReachesMatching()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001003";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "kitchen sink leak");
        await _fx.InjectTextAndAwaitAsync(phone, "no");

        var presented = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase));

        presented.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ServiceRequest_PickProvider_SharesContactBothSides()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001006";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "kitchen sink leak");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var presented = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("present-top-matches", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("Top matches", StringComparison.OrdinalIgnoreCase));

        const string topProviderPhone = "+2203000001";
        await _fx.InjectTextAndAwaitAsync(phone, "PICK 1");

        var clientShare = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #1: provider for ", StringComparison.Ordinal),
            since: presented.At);

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
        var phone = "+22070001010";

        // Free-text "give me the contact details" implies the requester is OK with
        // sharing — drive intake with consent=true so the bilateral-consent path
        // can reveal phones.
        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(phone, "give me the contact details");

        var ambiguity = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        ambiguity.Body.ShouldContain("Reply 1");

        const string topProviderPhone = "+2203000001";
        await _fx.InjectTextAndAwaitAsync(phone, "1");

        var clientShare = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Match #1: provider for ", StringComparison.Ordinal),
            since: ambiguity.At);

        clientShare.Body.ShouldContain("plumbing");
        clientShare.Body.ShouldContain(topProviderPhone);
    }

    [Fact]
    public async Task ServiceRequest_AddressText_GetsGeocodedAndConfirmed()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001004";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a carpenter");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectTextAndAwaitAsync(phone, "1 Market St San Francisco");

        var found = await client.ExpectOutboundAsync(phone, m => m.Body.StartsWith("Found:"));
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
        var phone = "+22070001007";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

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
        var phone = "+22070001008";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        var prompt = await client.ExpectOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        await _fx.InjectTextAndAwaitAsync(phone, "no");
        var afterNo = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("What service do you need", StringComparison.OrdinalIgnoreCase),
            since: prompt.At);

        afterNo.Body.ShouldNotContain("Reply YES or NO", Case.Insensitive);
    }

    [Fact]
    public async Task SecondMessageMidFunnel_ReprompstYesNo_NotRestartFunnel()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+22070001005";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        var first = await client.ExpectOutboundAsync(phone, m => m.Body.Contains("YES or NO"));

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");

        var reprompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase),
            since: first.At);
        reprompt.Body.ShouldNotContain("Do you need", Case.Insensitive);
    }

    [Fact]
    public async Task EndsSession_ClientFunnel_DeletesDraftAndReplies()
    {
        var phone = "+220700000091";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");

        using var client = _fx.Factory.CreateClient();
        await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Reply YES or NO", StringComparison.OrdinalIgnoreCase));

        await _fx.InjectTextAndAwaitAsync(phone, "bye");
        var ended = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Session ended", StringComparison.OrdinalIgnoreCase));
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

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "of course");

        var advanced = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Send your location", StringComparison.OrdinalIgnoreCase));
        advanced.ShouldNotBeNull();
    }

    // Regression for the "that's right." literal-table miss — QuickIntent.Detect
    // now strips trailing punctuation so the deterministic path advances without
    // hitting the LLM fallback.
    [Fact]
    public async Task TrailingPunctConfirmation_AdvancesClientFunnel_NoLlmCall()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000097";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "that's right.");

        var advanced = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Send your location", StringComparison.OrdinalIgnoreCase));
        advanced.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmService_LocationPin_RepromptsWithoutLlmCall()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000098";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectLocationAndAwaitAsync(phone, 13.4549, -16.5790);

        var reprompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Please reply YES or NO", StringComparison.OrdinalIgnoreCase));
        reprompt.ShouldNotBeNull();
        _fx.FakeAi.ExtractConfirmIntentCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ServiceRequest_DescriptionStep_AmbiguousReply_TreatedAsSkip()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000093";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);

        var descPrompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        // Ambiguous reply that is neither SKIP nor a real description.
        await _fx.InjectTextAndAwaitAsync(phone, "I'm good");
        await client.ExpectOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At);

        await _fx.InjectTextAndAwaitAsync(phone, "no");

        var lookingFor = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("Looking for", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At);
        lookingFor.ShouldNotBeNull();
    }

    // Regression: the description prompt is a contract — when we ask the user to
    // describe their problem, the next inbound IS the description, even if it
    // matches a service-request hint regex ("my X is broken", "X everywhere"...).
    // It must not be hijacked into a slug-switch + "Looking up the service…" ack.
    [Fact]
    public async Task ServiceRequest_DescriptionStep_ProblemStatement_CapturedAsDescription_NotSlugSwitched()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000095";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        var descPrompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("description", StringComparison.OrdinalIgnoreCase));

        // Trips both PossessiveProblemRx and DisasterRx in QuickIntent.
        const string description = "my tap is leaking and water everywhere";
        await _fx.InjectTextAndAwaitAsync(phone, description);

        await client.ExpectOutboundAsync(phone,
            m => m.Body.Contains("share your phone number", StringComparison.OrdinalIgnoreCase),
            since: descPrompt.At);

        using var scope = _fx.Factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IClientRequestDraftRepository>();
        var draft = await drafts.GetAsync(phone, default);
        draft.ShouldNotBeNull();
        draft.DraftDescription.ShouldBe(description);
    }

    [Fact]
    public async Task ServiceRequest_YesAfterPresent_RepromptsWhenMultiple()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000094";

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var ambiguity = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

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

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, reply);

        var followup = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.StartsWith("Which match", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.StartsWith("Match #", StringComparison.Ordinal),
            since: presented.At);

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

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(_fx, phone);

        await _fx.InjectTextAndAwaitAsync(phone, "NEW");

        var freshPrompt = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("what service do you need", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        freshPrompt.Body.ShouldContain("I need", Case.Insensitive);
    }

    [Fact]
    public async Task ServiceRequest_DeliveryFromMarketplaceContext_ConfirmsDelivery()
    {
        using var client = _fx.Factory.CreateClient();
        var phone = "+220700000095";

        await _fx.InjectTextAndAwaitAsync(
            phone,
            "I need to delivery what I bought from facebook marketplace");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase));

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

        await _fx.InjectTextAndAwaitAsync(phone, "I need a taxi to airport");

        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase));

        reply.Body.ShouldContain("ride", Case.Insensitive);
        reply.Body.ShouldNotContain("delivery", Case.Insensitive);
    }
}
