using Hook.Features.MetaTemplates;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task ColdPath_Question_ShortCircuitsToPlatformAnswer()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002005";

        await _fx.InjectTextAndAwaitAsync(phone, "how does this work?");

        // Cold-path dispatcher sends an ack BEFORE the AI answer so the user
        // doesn't stare at a silent WhatsApp window during the Ollama window.
        var ack = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("one sec", StringComparison.OrdinalIgnoreCase));

        // FakeConversationAi's stub replies "[stub-qa] {question}" verbatim.
        var reply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal),
            since: ack.At);

        reply.Body.ShouldContain("how does this work?");
    }

    [Fact]
    public async Task ColdPath_Question_RepeatedWithinDedupWindow_BothInboundsAnswered()
    {
        // Dedup is now best-effort AI-cost suppression on Wolverine envelope retry —
        // not a user-spam delivery gate. A user who re-asks the same question gets a
        // fresh answer (at-least-once delivery beats Ollama-cost suppression).
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002006";
        const string question = "what is hook for in my life?";

        await _fx.InjectTextAndAwaitAsync(phone, question);
        var firstReply = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal));

        await _fx.InjectTextAndAwaitAsync(phone, question);
        var secondReply = await client.ExpectOutboundAsync(
            phone,
            m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal),
            since: firstReply.At);

        secondReply.Body.ShouldContain(question);
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
    public async Task BareTip_DuringActiveClientDraft_RoutesToTipDispatcher_DraftSurvives()
    {
        // Regression: bare "tip" mid-draft used to reach ClientRequestOrchestrator,
        // which consumed the token as a service-description input. The early-route
        // above the draft check must hand the token to TipDispatcher and leave the
        // draft intact for the next inbound to resume.
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002007";

        await _fx.InjectTextAndAwaitAsync(phone, "I need a plumber");
        var confirmPrompt = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase));

        var drafts = _fx.Factory.Services.GetRequiredService<IClientRequestDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldNotBeNull();

        await _fx.InjectTextAndAwaitAsync(phone, "tip");

        // Outbound after the bare-tip inject: either the tip text or the
        // "No new tip right now…" fallback. Either way, NOT the orchestrator's
        // confirm/re-prompt (which would echo "YES or NO" again).
        var tipReply = await client.ExpectOutboundAsync(
            phone,
            m => m.At > confirmPrompt.At &&
                 !m.Body.Contains("YES or NO", StringComparison.OrdinalIgnoreCase));
        tipReply.Body.ShouldNotBeEmpty();

        // Draft survived the tip detour.
        (await drafts.GetAsync(phone, default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task BareTip_DuringActiveRegistrationDraft_RoutesToTipDispatcher_DraftSurvives()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002008";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        var confirmPrompt = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("I detected", StringComparison.OrdinalIgnoreCase)
                     || m.Body.Contains("YES", StringComparison.OrdinalIgnoreCase));

        var drafts = _fx.Factory.Services.GetRequiredService<IRegistrationDraftRepository>();
        (await drafts.GetAsync(phone, default)).ShouldNotBeNull();

        await _fx.InjectTextAndAwaitAsync(phone, "any tips");

        var tipReply = await client.ExpectOutboundAsync(
            phone,
            m => m.At > confirmPrompt.At &&
                 !m.Body.Contains("I detected", StringComparison.OrdinalIgnoreCase));
        tipReply.Body.ShouldNotBeEmpty();

        (await drafts.GetAsync(phone, default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ColdPath_Question_PreferredLocaleFrench_DispatchesWithFrenchLocale()
    {
        // Locale unpin regression: classifier persists PreferredLocale on every
        // LLM-routed inbound; the deterministic cold-Q&A short-circuit must read
        // it instead of hard-coding "en". Seed the row directly so we don't have
        // to run an LLM round trip just to set the locale.
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002009";

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var contacts = scope.ServiceProvider.GetRequiredService<IWhatsappContactRepository>();
            await contacts.UpsertInboundAsync(phone, DateTimeOffset.UtcNow);
            await contacts.UpdatePreferredLocaleAsync(phone, "fr");
        }
        _fx.FakeAi.ResetOverrides();

        await _fx.InjectTextAndAwaitAsync(phone, "how does this work?");

        await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal));

        _fx.FakeAi.LastAnswerPlatformQuestionLocale.ShouldBe("fr");
    }

    [Fact]
    public async Task ColdPath_Question_NoPriorLocale_FallsBackToEn()
    {
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002010";
        _fx.FakeAi.ResetOverrides();

        await _fx.InjectTextAndAwaitAsync(phone, "how does this work?");

        await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("[stub-qa]", StringComparison.Ordinal));

        _fx.FakeAi.LastAnswerPlatformQuestionLocale.ShouldBe("en");
    }

    [Fact]
    public async Task ListedProvider_BareTip_ExtendsTtlBeforeDispatch()
    {
        // Heartbeat parity: every inbound from a listed provider extends the
        // TTL (Greeting/Unknown already do this further down). The early-route
        // bare-tip arm must heartbeat silently first; otherwise a listed
        // provider asking for a tip silently lets the listing expire.
        using var client = _fx.Factory.CreateClient();
        const string phone = "+22070002099";

        await _fx.InjectTextAndAwaitAsync(phone, "I offer carpentry");
        await _fx.InjectTextAndAwaitAsync(phone, "yes");
        await _fx.InjectLocationAndAwaitAsync(phone, DevPipelineFixture.SeedRefLat, DevPipelineFixture.SeedRefLng);
        await _fx.InjectTextAndAwaitAsync(phone, "yes");

        var listed = await client.ExpectOutboundAsync(
            phone, m => m.Body.Contains("listed for", StringComparison.OrdinalIgnoreCase));

        DateTimeOffset preTipExpiresAt;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            var pre = await providers.GetAsync(phone, default);
            pre.ShouldNotBeNull();
            preTipExpiresAt = pre.ExpiresAt;
        }

        await _fx.InjectTextAndAwaitAsync(phone, "tip");

        var tipReply = await client.ExpectOutboundAsync(
            phone, m => m.At > listed.At);
        tipReply.Body.ShouldNotBeEmpty();

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IProviderAvailabilityRepository>();
            var post = await providers.GetAsync(phone, default);
            post.ShouldNotBeNull();
            post.ExpiresAt.ShouldBeGreaterThan(preTipExpiresAt,
                "Heartbeat should have advanced ExpiresAt past the pre-tip value.");
        }
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
