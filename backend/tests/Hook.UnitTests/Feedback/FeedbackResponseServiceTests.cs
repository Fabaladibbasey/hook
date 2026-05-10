using Hook.Features.Ai;
using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hook.UnitTests.Feedback;

public class FeedbackResponseServiceTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Y")]
    [InlineData("  yes  ")]
    public void ParseAnswer_YesVariants_ReturnsYes(string input) =>
        Assert.Equal(FeedbackAnswer.Yes, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("NO")]
    public void ParseAnswer_NoVariants_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("in progress")]
    [InlineData("IN PROGRESS")]
    [InlineData("yes, in progress actually")]
    [InlineData("the work is in progress")]
    public void ParseAnswer_InProgress_ReturnsInProgress(string input) =>
        Assert.Equal(FeedbackAnswer.InProgress, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("not in progress")]
    [InlineData("NOT IN PROGRESS")]
    [InlineData("the job is not in progress at all")]
    public void ParseAnswer_NotInProgress_ReturnsNo(string input) =>
        Assert.Equal(FeedbackAnswer.No, FeedbackResponseService.ParseAnswer(input));

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("idk")]
    public void ParseAnswer_Unrecognised_ReturnsNull(string input) =>
        Assert.Null(FeedbackResponseService.ParseAnswer(input));

    [Fact]
    public void ParseAnswer_Whitespace_ReturnsNull() =>
        Assert.Null(FeedbackResponseService.ParseAnswer("   "));

    [Theory]
    [InlineData("1", 3, 1)]
    [InlineData("  2  ", 3, 2)]
    [InlineData("3.", 3, 3)]
    [InlineData("99", 3, null)]      // out of range
    [InlineData("0", 3, null)]       // zero invalid
    [InlineData("abc", 3, null)]
    [InlineData("", 3, null)]
    [InlineData("I pick 2", 3, 2)]
    [InlineData("call me at 3 pm", 2, null)]   // 3 out of range with picks=2
    [InlineData("1, 2", 3, 1)]
    public void ParsePickDigit_Cases(string text, int max, int? expected) =>
        Assert.Equal(expected, FeedbackResponseService.ParsePickDigit(text, max));

    // -- HandleAsync: switch fails loudly on unknown FeedbackStep -------------

    [Fact]
    public async Task HandleAsync_UnknownStep_ThrowsInvalidOperation()
    {
        var deps = new Deps();
        var pending = new MatchFeedback { MatchId = Guid.NewGuid(), Step = (FeedbackStep)999 };
        var service = deps.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None));
    }

    // -- HandleDidYouFindAsync ------------------------------------------------

    [Fact]
    public async Task DidYouFind_InProgressReply_DoesNotClaimAndAsksRetry()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("in progress"), pending, deps.Intent("in progress"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("YES if you found", deps.Whatsapp.Sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_YesSinglePick_PublishesStep2()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        var service = deps.Build();

        await service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Yes, deps.Feedback.Claimed[0].Answer);
        Assert.Single(deps.Bus.Published);
        Assert.Equal(pending.MatchId, ((Step2FeedbackCheck)deps.Bus.Published[0]).MatchId);
        Assert.Empty(deps.Bus.Scheduled);
    }

    [Fact]
    public async Task DidYouFind_YesMultiPick_ReservesIdentifyWinnerAndSendsPrompt()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        var service = deps.Build();

        await service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None);

        Assert.Single(deps.Feedback.Added);
        Assert.Equal(FeedbackStep.IdentifyWinner, deps.Feedback.Added[0].Step);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("Which provider", deps.Whatsapp.Sent[0].Body);
        Assert.Contains("1)", deps.Whatsapp.Sent[0].Body);
        Assert.Contains("2)", deps.Whatsapp.Sent[0].Body);
        // Step1 claim happens AFTER the send (so a send-failure leaves Step1 Pending).
        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Yes, deps.Feedback.Claimed[0].Answer);
        // No Step2 publish — winner identification is still required.
        Assert.Empty(deps.Bus.Published);
    }

    [Fact]
    public async Task DidYouFind_YesMultiPick_SendThrows_LeavesStep1PendingAndDeletesIdentifyWinner()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        deps.Whatsapp.ThrowOnSend = new InvalidOperationException("transport down");
        var service = deps.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None));

        // IdentifyWinner row was added then deleted; Step1 stays Pending (no claim).
        Assert.Single(deps.Feedback.Added);
        Assert.Single(deps.Feedback.Deleted);
        Assert.Empty(deps.Feedback.Claimed);
    }

    [Fact]
    public async Task DidYouFind_YesNoPickedSiblings_LogsAndClaims()
    {
        // PickedAt cleared on anchor between Step1 schedule and reply — claim and exit.
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        // Don't set PickedAt — picked.Count == 0
        var service = deps.Build();

        await service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Yes, deps.Feedback.Claimed[0].Answer);
        Assert.Empty(deps.Bus.Published);
        Assert.Empty(deps.Whatsapp.Sent);
    }

    [Fact]
    public async Task DidYouFind_NoReply_ClaimsNoNoPublish()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("no"), pending, deps.Intent("no"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.No, deps.Feedback.Claimed[0].Answer);
        Assert.Empty(deps.Bus.Published);
        Assert.Empty(deps.Whatsapp.Sent);
    }

    [Fact]
    public async Task DidYouFind_GarbageWithinRetryWindow_SendsHint()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending(promptedAt: deps.Clock.GetUtcNow());
        var service = deps.Build();

        await service.HandleAsync(NewInbound("xyz"), pending, deps.Intent("xyz"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("Sorry, didn't catch that", deps.Whatsapp.Sent[0].Body);
    }

    [Fact]
    public async Task DidYouFind_GarbagePastRetryWindow_SilentNoAiCall()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending(
            promptedAt: deps.Clock.GetUtcNow() - TimeSpan.FromHours(2)); // > ParseRetryWindow
        var service = deps.Build();
        var intent = deps.Intent("xyz");

        await service.HandleAsync(NewInbound("xyz"), pending, intent, CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Empty(deps.Whatsapp.Sent);
        // Bound the AI fallback by the retry window — no Ollama call for stale Pending.
        Assert.Equal(0, deps.Ai.DetectIntentCalls);
    }

    [Fact]
    public async Task DidYouFind_TryClaimRaceLost_NoPublish()
    {
        var deps = new Deps();
        var pending = deps.SeedDidYouFindPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Feedback.TryClaimResult = false;
        var service = deps.Build();

        await service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None);

        Assert.Empty(deps.Bus.Published);
    }

    // -- HandleIdentifyWinnerAsync --------------------------------------------

    [Fact]
    public async Task IdentifyWinner_ZeroPickedSiblings_Silent()
    {
        var deps = new Deps();
        var pending = deps.SeedIdentifyWinnerPending();
        // Anchor has no PickedAt — picked.Count == 0
        var service = deps.Build();

        await service.HandleAsync(NewInbound("1"), pending, deps.Intent("1"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Empty(deps.Whatsapp.Sent);
    }

    [Fact]
    public async Task IdentifyWinner_ValidDigit_ClaimsWinnerSelectedAndPublishesForWinner()
    {
        var deps = new Deps();
        var pending = deps.SeedIdentifyWinnerPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        var sibling = deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        // Two picked siblings; reply "2" picks the second (anchor).
        var service = deps.Build();

        await service.HandleAsync(NewInbound("2"), pending, deps.Intent("2"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.WinnerSelected, deps.Feedback.Claimed[0].Answer);
        Assert.Single(deps.Bus.Published);
        // Production order from FakeMatchRepository: sibling=highest score first.
        // sibling is sorted first (higher Score by default seed), anchor is second.
        var winnerId = ((Step2FeedbackCheck)deps.Bus.Published[0]).MatchId;
        Assert.Equal(anchor.Id, winnerId);
        _ = sibling;
    }

    [Fact]
    public async Task IdentifyWinner_OutOfRangeWithinRetryWindow_SendsHint()
    {
        var deps = new Deps();
        var pending = deps.SeedIdentifyWinnerPending(promptedAt: deps.Clock.GetUtcNow());
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        var service = deps.Build();

        await service.HandleAsync(NewInbound("99"), pending, deps.Intent("99"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("Reply with the number", deps.Whatsapp.Sent[0].Body);
    }

    [Fact]
    public async Task IdentifyWinner_PastRetryWindow_ClaimsSkippedAndLogs()
    {
        var deps = new Deps();
        var pending = deps.SeedIdentifyWinnerPending(
            promptedAt: deps.Clock.GetUtcNow() - TimeSpan.FromHours(2));
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        var service = deps.Build();

        await service.HandleAsync(NewInbound("xyz"), pending, deps.Intent("xyz"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Skipped, deps.Feedback.Claimed[0].Answer);
        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Bus.Published);
    }

    [Fact]
    public async Task IdentifyWinner_TryClaimRaceLost_NoPublish()
    {
        var deps = new Deps();
        var pending = deps.SeedIdentifyWinnerPending();
        var anchor = deps.Matches.AnchorOf(pending.MatchId);
        anchor.PickedAt = DateTimeOffset.UtcNow;
        deps.Matches.SeedSibling(pending.MatchId, "+2204445678");
        deps.Feedback.TryClaimResult = false;
        var service = deps.Build();

        await service.HandleAsync(NewInbound("1"), pending, deps.Intent("1"), CancellationToken.None);

        Assert.Empty(deps.Bus.Published);
    }

    // -- HandleJobCompletedAsync ----------------------------------------------

    [Fact]
    public async Task JobCompleted_Yes_RecordsOutcomeSuccess()
    {
        var deps = new Deps();
        var pending = deps.SeedJobCompletedPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("yes"), pending, deps.Intent("yes"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Yes, deps.Feedback.Claimed[0].Answer);
        Assert.NotNull(deps.Feedback.LastUpsertedStats);
        Assert.Equal(1, deps.Feedback.LastUpsertedStats!.SuccessCount);
    }

    [Fact]
    public async Task JobCompleted_No_RecordsOutcomeFailure()
    {
        var deps = new Deps();
        var pending = deps.SeedJobCompletedPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("no"), pending, deps.Intent("no"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.No, deps.Feedback.Claimed[0].Answer);
        Assert.NotNull(deps.Feedback.LastUpsertedStats);
        Assert.Equal(0, deps.Feedback.LastUpsertedStats!.SuccessCount);
        Assert.Equal(1, deps.Feedback.LastUpsertedStats!.CompletedCount);
    }

    [Fact]
    public async Task JobCompleted_InProgress_ReservesAwaitingEtaAndAsks()
    {
        var deps = new Deps();
        var pending = deps.SeedJobCompletedPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("in progress"), pending, deps.Intent("in progress"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.InProgress, deps.Feedback.Claimed[0].Answer);
        Assert.Single(deps.Feedback.Added);
        Assert.Equal(FeedbackStep.AwaitingEta, deps.Feedback.Added[0].Step);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("when do you think", deps.Whatsapp.Sent[0].Body);
    }

    [Fact]
    public async Task JobCompleted_InProgress_SendThrows_DeletesAwaitingEta()
    {
        var deps = new Deps();
        var pending = deps.SeedJobCompletedPending();
        deps.Whatsapp.ThrowOnSend = new InvalidOperationException("transport down");
        var service = deps.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HandleAsync(NewInbound("in progress"), pending, deps.Intent("in progress"), CancellationToken.None));

        Assert.Single(deps.Feedback.Added);
        Assert.Single(deps.Feedback.Deleted);
    }

    [Fact]
    public async Task JobCompleted_GarbagePastRetryWindow_SilentNoAiCall()
    {
        var deps = new Deps();
        var pending = deps.SeedJobCompletedPending(
            promptedAt: deps.Clock.GetUtcNow() - TimeSpan.FromHours(2));
        var service = deps.Build();

        await service.HandleAsync(NewInbound("xyz"), pending, deps.Intent("xyz"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Equal(0, deps.Ai.DetectIntentCalls);
    }

    // -- HandleAwaitingEtaAsync -----------------------------------------------

    [Fact]
    public async Task AwaitingEta_ValidFutureEta_ClaimsEtaCapturedAndSchedules()
    {
        var deps = new Deps();
        var pending = deps.SeedAwaitingEtaPending();
        var now = deps.Clock.GetUtcNow();
        var expectedEta = now + TimeSpan.FromHours(3);
        // FakeConversationAi heuristic already maps "in 3 hours" → now + 3h.
        var service = deps.Build();

        await service.HandleAsync(NewInbound("in 3 hours"), pending, deps.Intent("in 3 hours"), CancellationToken.None);

        Assert.Single(deps.Feedback.ClaimedWithEta);
        var claim = deps.Feedback.ClaimedWithEta[0];
        Assert.Equal(FeedbackAnswer.EtaCaptured, claim.Answer);
        Assert.Equal(expectedEta, claim.EtaUtc);
        Assert.Single(deps.Bus.Scheduled);
        var (schedMsg, schedDelay) = deps.Bus.Scheduled[0];
        Assert.Equal(pending.MatchId, ((Step2FeedbackCheck)schedMsg).MatchId);
        Assert.True(schedDelay >= TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task AwaitingEta_AiNullWithinRetryWindow_SendsHint()
    {
        var deps = new Deps();
        var pending = deps.SeedAwaitingEtaPending();
        // Override heuristic so "in 3 hours" returns null instead of now+3h.
        deps.Ai.OverrideEta("in 3 hours", null);
        var service = deps.Build();

        await service.HandleAsync(NewInbound("in 3 hours"), pending, deps.Intent("in 3 hours"), CancellationToken.None);

        Assert.Empty(deps.Feedback.Claimed);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("when do you think", deps.Whatsapp.Sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwaitingEta_PastRetryWindow_ClaimsSkippedAndSchedulesFallback()
    {
        var deps = new Deps();
        var pending = deps.SeedAwaitingEtaPending(
            promptedAt: deps.Clock.GetUtcNow() - TimeSpan.FromHours(2));
        // "xyz" is < 4 chars after trim wouldn't trigger — but len 3, so LooksLikeEtaCandidate
        // rejects pre-AI. Heuristic also returns null. Either way: eta is null past window.
        var service = deps.Build();

        await service.HandleAsync(NewInbound("xyz"), pending, deps.Intent("xyz"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Skipped, deps.Feedback.Claimed[0].Answer);
        Assert.Single(deps.Bus.Scheduled);
        var (_, delay) = deps.Bus.Scheduled[0];
        Assert.Equal(deps.Options.Step2InProgressRecheckDelay, delay);
    }

    [Fact]
    public async Task AwaitingEta_AiReturnsBeyondHorizon_FallsBackAsSkipped()
    {
        var deps = new Deps();
        var pending = deps.SeedAwaitingEtaPending();
        // FakeConversationAi heuristic maps "in 99 days" → now + 99d (> MaxEtaHorizon=7d).
        var service = deps.Build();

        await service.HandleAsync(NewInbound("in 99 days"), pending, deps.Intent("in 99 days"), CancellationToken.None);

        Assert.Single(deps.Feedback.Claimed);
        Assert.Equal(FeedbackAnswer.Skipped, deps.Feedback.Claimed[0].Answer);
        Assert.Single(deps.Bus.Scheduled);
        var (_, delay) = deps.Bus.Scheduled[0];
        Assert.Equal(deps.Options.Step2InProgressRecheckDelay, delay);
    }

    [Fact]
    public async Task AwaitingEta_ShortGarbageReply_SkipsAiCall()
    {
        // E9 precheck: short reply with no digits and no ETA keyword should never
        // hit Ollama — saves a round trip on hostile input.
        var deps = new Deps();
        var pending = deps.SeedAwaitingEtaPending();
        var service = deps.Build();

        await service.HandleAsync(NewInbound("ok"), pending, deps.Intent("ok"), CancellationToken.None);

        Assert.Equal(0, deps.Ai.ExtractEtaCalls);
    }

    // ------------------------------------------------------------------------

    private static InboundMessage NewInbound(string text) =>
        new(MessageId: "m-" + Guid.NewGuid(),
            From: PhoneNumber.Parse("+2203339999"),
            Timestamp: DateTimeOffset.UtcNow,
            Kind: InboundMessageKind.Text,
            Text: text,
            Location: null,
            RawJson: null);

    private sealed class Deps
    {
        public FakeFeedbackRepository Feedback { get; } = new();
        public FakeMatchRepository Matches { get; } = new();
        public FakeConversationAi Ai { get; } = new();
        public FakeEventBus Bus { get; } = new();
        public FakeWhatsapp Whatsapp { get; } = new();
        public FakeTimeProvider Clock { get; } = new();
        public FeedbackOptions Options { get; } = new();

        public LazyIntent Intent(string text) => new(Ai, text);

        public FeedbackResponseService Build() =>
            new(Feedback, Matches, Ai, Bus, Whatsapp,
                Microsoft.Extensions.Options.Options.Create(Options),
                Clock,
                NullLogger<FeedbackResponseService>.Instance);

        public MatchFeedback SeedDidYouFindPending(DateTimeOffset? promptedAt = null) =>
            SeedPendingForStep(FeedbackStep.DidYouFind, promptedAt);

        public MatchFeedback SeedIdentifyWinnerPending(DateTimeOffset? promptedAt = null) =>
            SeedPendingForStep(FeedbackStep.IdentifyWinner, promptedAt);

        public MatchFeedback SeedJobCompletedPending(DateTimeOffset? promptedAt = null) =>
            SeedPendingForStep(FeedbackStep.JobCompleted, promptedAt);

        public MatchFeedback SeedAwaitingEtaPending(DateTimeOffset? promptedAt = null) =>
            SeedPendingForStep(FeedbackStep.AwaitingEta, promptedAt);

        private MatchFeedback SeedPendingForStep(FeedbackStep step, DateTimeOffset? promptedAt)
        {
            var anchor = Matches.SeedAnchor("+2203331234");
            return new MatchFeedback
            {
                MatchId = anchor.Id,
                Step = step,
                PromptedAt = promptedAt ?? Clock.GetUtcNow()
            };
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixed = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _fixed;
    }

    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        public List<MatchFeedback> Added { get; } = new();
        public List<Guid> Deleted { get; } = new();
        public List<(Guid Id, FeedbackAnswer Answer)> Claimed { get; } = new();
        public List<(Guid Id, FeedbackAnswer Answer, DateTimeOffset EtaUtc)> ClaimedWithEta { get; } = new();
        public bool TryAddResult { get; set; } = true;
        public bool TryClaimResult { get; set; } = true;
        public ProviderStats? LastUpsertedStats { get; private set; }

        public Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);
        public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);
        public Task<MatchFeedback?> GetPendingAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);
        public Task<MatchFeedback?> GetLatestByMatchAndStepAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);
        public Task<bool> AnyByRequestStepAsync(Guid requestId, FeedbackStep step, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryClaimPendingAsync(Guid feedbackId, FeedbackAnswer answer, DateTimeOffset now, CancellationToken ct = default)
        {
            if (TryClaimResult) Claimed.Add((feedbackId, answer));
            return Task.FromResult(TryClaimResult);
        }

        public Task<bool> TryClaimPendingWithEtaAsync(Guid feedbackId, FeedbackAnswer answer, DateTimeOffset etaUtc, DateTimeOffset now, CancellationToken ct = default)
        {
            if (TryClaimResult) ClaimedWithEta.Add((feedbackId, answer, etaUtc));
            return Task.FromResult(TryClaimResult);
        }

        public Task AddAsync(MatchFeedback feedback, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default)
        {
            if (TryAddResult) Added.Add(feedback);
            return Task.FromResult(TryAddResult);
        }

        public Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default)
        {
            Deleted.Add(feedbackId);
            return Task.FromResult(true);
        }

        public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
            Task.FromResult<ProviderStats?>(null);

        public Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default)
        {
            LastUpsertedStats = stats;
            return Task.CompletedTask;
        }

        public Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMatchRepository : IMatchRepository
    {
        public Dictionary<Guid, Match> Stored { get; } = new();
        public Dictionary<Guid, List<Match>> RequestMatches { get; } = new();

        public Match SeedAnchor(string providerPhone)
        {
            var m = new Match
            {
                RequestId = Guid.NewGuid(),
                ProviderPhone = providerPhone,
                ServiceSlug = "plumbing"
            };
            Stored[m.Id] = m;
            RequestMatches[m.RequestId] = new List<Match> { m };
            return m;
        }

        public Match SeedSibling(Guid anchorMatchId, string providerPhone)
        {
            var anchor = Stored[anchorMatchId];
            var sibling = new Match
            {
                RequestId = anchor.RequestId,
                ProviderPhone = providerPhone,
                ServiceSlug = anchor.ServiceSlug,
                CreatedAt = anchor.CreatedAt.AddSeconds(-1),
                Score = anchor.Score + 0.1, // ranks higher → first in production order
                PickedAt = DateTimeOffset.UtcNow
            };
            Stored[sibling.Id] = sibling;
            // Production order: Score DESC -> sibling first, anchor second.
            RequestMatches[anchor.RequestId] = new List<Match> { sibling, anchor };
            return sibling;
        }

        public Match AnchorOf(Guid matchId) => Stored[matchId];

        public Task<Match?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var m) ? m : null);

        public Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Match>>(
                RequestMatches.TryGetValue(requestId, out var l) ? l : new List<Match>());

        public Task AddAsync(Match match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default) => Task.FromResult(true);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEventBus : IEventBus
    {
        public List<object> Published { get; } = new();
        public List<(object Message, TimeSpan Delay)> Scheduled { get; } = new();

        public Task PublishAsync<T>(T message, CancellationToken ct = default)
        {
            Published.Add(message!);
            return Task.CompletedTask;
        }

        public Task ScheduleAsync<T>(T message, TimeSpan delay, CancellationToken ct = default)
        {
            Scheduled.Add((message!, delay));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWhatsapp : IWhatsappClient
    {
        public List<(PhoneNumber To, string Body)> Sent { get; } = new();
        public Exception? ThrowOnSend { get; set; }

        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            if (ThrowOnSend is not null) throw ThrowOnSend;
            Sent.Add((to, body));
            return Task.FromResult("msg-1");
        }
    }
}
