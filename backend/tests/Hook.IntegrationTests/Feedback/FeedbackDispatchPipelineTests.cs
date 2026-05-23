using Hook.Features.Feedback.Models;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Feedback;

[Collection("Pipeline-2")]
public sealed class FeedbackDispatchPipelineTests : PipelineTestBase
{
    public FeedbackDispatchPipelineTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task ContactExchanged_DispatchesStep1_ThenYesDispatchesStep2()
    {
        // Both-consent client + provider #1 (ShareContact=true seeded) so PhoneExchanger
        // publishes ContactExchangedEvent on PICK 1 → Step1FeedbackCheck → Step1 prompt.
        // Fixture pins Feedback:Step1InitialDelay=00:00:00 so the Wolverine scheduler
        // dispatches the prompt synchronously. Step2 publishes immediately on Step1=Yes
        // (no separate delay knob).
        const string clientPhone = "+22070007001";
        const string consentingProvider = "+2203000001";

        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));

        // ContactExchangedEvent → handler schedules Step1 with TimeSpan.Zero. TrackActivity
        // does not always await scheduled-message dispatches, so poll.
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var pending = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.DidYouFind && f.Answer == FeedbackAnswer.Pending)
                .ToListAsync();
            pending.ShouldHaveSingleItem();
        }

        // Sanity: provider #1 received the phone-reveal notice (proves the bilateral
        // exchange path actually published ContactExchangedEvent, not just a chat-route).
        await http.ExpectOutboundAsync(
            consentingProvider,
            m => m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase) &&
                 m.Body.Contains(clientPhone, StringComparison.Ordinal),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");

        var step2 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: step1.At);

        step2.Body.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PickAll_DispatchesSingleStep1Prompt_WinnerReplyTriggersStep2()
    {
        // Multi-PICK (PICK ALL → 3 matches): Step1 dedupes per-request so the client
        // only sees ONE "did any work out?" prompt instead of three. The reply path
        // then asks "which provider?" and routes Step2 to the chosen match — so
        // ProviderStats credit lands on the actual completing provider.
        const string clientPhone = "+22070007003";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK ALL", timeout: TimeSpan.FromSeconds(20));

        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        // Per-request dedupe: only one Step1 prompt for three picked matches.
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var step1Rows = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.DidYouFind)
                .ToListAsync();
            step1Rows.ShouldHaveSingleItem();
        }

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");

        // Multi-pick path: instead of jumping to Step2, bot asks which provider.
        var whichPrompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("Which provider", StringComparison.OrdinalIgnoreCase),
            since: step1.At);
        whichPrompt.Body.ShouldContain("1)");
        whichPrompt.Body.ShouldContain("2)");
        whichPrompt.Body.ShouldContain("3)");

        await _fx.InjectTextAndAwaitAsync(clientPhone, "1");

        // After winner is identified, Step2 fires immediately for that match.
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: whichPrompt.At);

        // Step2's Pending JobCompleted row points at the WINNER match — not the anchor.
        // Reply "1" picks the first slot in production order (Score DESC, …); cross-check
        // the row's MatchId is the same one that ranked first in MatchRepository.
        await using var winnerScope = _fx.Factory.Services.CreateAsyncScope();
        var winnerCtx = winnerScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var clientReq = await winnerCtx.Set<Features.ServiceRequest.RequestAggregate.ServiceRequest>()
            .FirstAsync(r => r.ClientPhone == clientPhone);
        var picked = await winnerCtx.Set<Features.Matching.MatchAggregate.Match>()
            .Where(m => m.RequestId == clientReq.Id && m.PickedAt != null)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.DistanceKm)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
        picked.Count.ShouldBeGreaterThan(0);
        var expectedWinner = picked[0];

        var step2Pending = await winnerCtx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted && f.Answer == FeedbackAnswer.Pending)
            .ToListAsync();
        step2Pending.ShouldHaveSingleItem();
        step2Pending[0].MatchId.ShouldBe(expectedWinner.Id);
    }

    [Fact]
    public async Task Step2InProgress_AsksForEta()
    {
        // Pillar B: when the client says "in progress" to Step2, bot follows up
        // with an ETA prompt (a new AwaitingEta MatchFeedback row) instead of the
        // older blind 48h reschedule.
        const string clientPhone = "+22070007004";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));

        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: step1.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "in progress");
        var etaPrompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("when do you think", StringComparison.OrdinalIgnoreCase),
            since: step2.At);
        etaPrompt.Body.ShouldNotBeNullOrWhiteSpace();

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var awaiting = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.AwaitingEta && f.Answer == FeedbackAnswer.Pending)
                .ToListAsync();
            awaiting.ShouldHaveSingleItem();
        }

        // Reply with a parseable ETA — FakeConversationAi resolves "in 3 hours" via
        // its relative-phrase heuristic. The handler claims the AwaitingEta row as
        // EtaCaptured, persists EtaUtc, and schedules a follow-up Step2 recheck.
        await _fx.InjectTextAndAwaitAsync(clientPhone, "in 3 hours");

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var captured = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.AwaitingEta && f.Answer == FeedbackAnswer.EtaCaptured)
                .ToListAsync();
            captured.ShouldHaveSingleItem();
            captured[0].EtaUtc.ShouldNotBeNull();
            // FakeConversationAi resolves "in 3 hours" relative to now ⇒ EtaUtc is in
            // the future and within the MaxEtaHorizon window.
            captured[0].EtaUtc!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task ChatRoutedMatch_AlsoDispatchesStep1Feedback()
    {
        // Provider #2 (+2203000002) seeds with ShareContact=false, so PICK 2 here
        // routes to chat (publishes RouteMatchToChatCommand instead of ContactExchangedEvent).
        // ChatRoutingFeedbackScheduler in the Feedback slice must still schedule Step1.
        const string clientPhone = "+22070007002";
        const string nonConsentingProvider = "+2203000002";

        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);

        // Pick provider #2 specifically (the one with ShareContact=false).
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 2", timeout: TimeSpan.FromSeconds(20));

        // Confirm we actually took the chat-route path (not bilateral exchange).
        await http.ExpectOutboundAsync(
            clientPhone,
            m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("chat is ready", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        // Positive proof we actually went chat-route: the chat-routed provider must
        // NOT have received the bilateral phone-reveal notice. The original
        // `ShouldNotBeNullOrEmpty` was a tautology over a const string and asserted
        // nothing about the runtime path.
        var outbox = await http.GetOutboxAsync();
        outbox.Where(m => m.At > presented.At &&
                          m.To == nonConsentingProvider &&
                          m.Body.StartsWith("Client wants ", StringComparison.OrdinalIgnoreCase))
              .ShouldBeEmpty();
    }
}
