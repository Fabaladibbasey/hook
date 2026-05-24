using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatLifecycle.ProductiveSilence;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step1Intent;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Shared.Persistence.Data;
using Hook.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;

namespace Hook.IntegrationTests.Feedback;

[Collection("Pipeline-2")]
public sealed class Step1SmarterParserPipelineTests : PipelineTestBase
{
    public Step1SmarterParserPipelineTests(DevPipelineFixture fx) : base(fx)
    {
        // The fake AI is registered as a singleton on the shared Pipeline-2 fixture;
        // OverrideStep1Intent mutations leak across xUnit test order without an
        // explicit reset between tests.
        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<Hook.Features.Ai.IConversationAi>();
        fake.ResetOverrides();
    }

    [Fact]
    public async Task Step1_No_OpensCaptureNoReasonFollowUpAndPersistsReason()
    {
        const string clientPhone = "+22070008001";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "no");

        var followUp = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("What made it hard", StringComparison.OrdinalIgnoreCase),
            since: step1.At);

        // Free-text reason captured.
        await _fx.InjectTextAndAwaitAsync(clientPhone, "prices were too high");

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var captured = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.CaptureNoReason
                     && f.Answer == FeedbackAnswer.NoReasonCaptured)
            .ToListAsync();
        captured.ShouldHaveSingleItem();
        captured[0].NoReason.ShouldBe("prices were too high");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("that helps us improve", StringComparison.OrdinalIgnoreCase),
            since: followUp.At);
    }

    [Fact]
    public async Task Step1_NoThenSkip_LeavesNoReasonNull()
    {
        const string clientPhone = "+22070008002";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "no");
        // Wait for the CaptureNoReason follow-up to commit before SKIP, otherwise
        // SKIP races the NoReason Pending row insert and the assertion below races
        // the follow-up not yet being open.
        var followUp = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("What made it hard", StringComparison.OrdinalIgnoreCase),
            since: step1.At);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "SKIP");
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("that helps us improve", StringComparison.OrdinalIgnoreCase),
            since: followUp.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var captured = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.CaptureNoReason
                     && f.Answer == FeedbackAnswer.NoReasonCaptured)
            .ToListAsync();
        captured.ShouldHaveSingleItem();
        captured[0].NoReason.ShouldBeNull();
    }

    [Fact]
    public async Task Step1_StopAsking_ClaimsSkippedAndAcksOnce()
    {
        const string clientPhone = "+22070008003";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "stop asking me");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("won't ask about this one again", StringComparison.OrdinalIgnoreCase),
            since: step1.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rows = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.DidYouFind)
            .ToListAsync();
        rows.ShouldHaveSingleItem();
        rows[0].Answer.ShouldBe(FeedbackAnswer.Skipped);
        // No CaptureNoReason row should have been opened.
        var followUps = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.CaptureNoReason)
            .ToListAsync();
        followUps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Step1_RescheduleNoEta_BumpsRecheckCount()
    {
        const string clientPhone = "+22070008004";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "still looking");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("check back", StringComparison.OrdinalIgnoreCase),
            since: step1.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var rows = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.DidYouFind)
            .ToListAsync();
        rows.ShouldHaveSingleItem();
        rows[0].Answer.ShouldBe(FeedbackAnswer.Pending);
        rows[0].Step1RecheckCount.ShouldBe(1);
    }

    [Fact]
    public async Task Step1_AiFallback_StopAsking_ClaimsSkippedFromApplyHandler()
    {
        // Pre-seed the fake AI to classify a non-deterministic reply as StopAsking.
        const string clientPhone = "+22070008005";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        var step1 = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        // FakeConversationAi default heuristics map "xyz" to Unclear; override to StopAsking.
        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<Hook.Features.Ai.IConversationAi>();
        fake.OverrideStep1Intent("really annoying", new Step1ParseResult(Step1ReplyIntent.StopAsking, null));

        await _fx.InjectTextAndAwaitAsync(clientPhone, "really annoying");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("won't ask about this one again", StringComparison.OrdinalIgnoreCase),
            since: step1.At);
    }

    [Fact]
    public async Task ProductiveSilence_FiresStep1_AndGateBlocksDoubleFire()
    {
        // Build a chat-routed match (non-consenting provider), seed message activity,
        // then invoke ProductiveSilenceCheck twice — only the first should publish.
        const string clientPhone = "+22070008006";
        const string nonConsentingProvider = "+2203000002";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 2", timeout: TimeSpan.FromSeconds(20));

        await http.ExpectOutboundAsync(
            clientPhone,
            m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("chat is ready", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        Guid chatId;
        Guid clientParticipantId;
        Guid providerParticipantId;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var match = await ctx.Set<Features.Matching.MatchAggregate.Match>()
                .FirstAsync(m => m.ProviderPhone == nonConsentingProvider && m.ChatId != null);
            chatId = match.ChatId!.Value;
            clientParticipantId = (await ctx.Set<Features.ChatSession.ParticipantAggregate.ChatParticipant>()
                .FirstAsync(p => p.ChatId == chatId
                              && p.Role == Features.ChatSession.ParticipantAggregate.ChatParticipantRole.Client)).Id;
            providerParticipantId = (await ctx.Set<Features.ChatSession.ParticipantAggregate.ChatParticipant>()
                .FirstAsync(p => p.ChatId == chatId
                              && p.Role == Features.ChatSession.ParticipantAggregate.ChatParticipantRole.Provider)).Id;
        }

        // Seed 3 messages per side directly through the repo so ProductiveSilenceHandler
        // sees the conversation min-threshold cleared. Bypassing ChatHub keeps the test
        // off the SignalR stack.
        await SeedChatMessagesAsync(chatId, clientParticipantId, providerParticipantId, 3);

        var firstActivity = await GetLastActivityAsync(chatId);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new ProductiveSilenceCheck(chatId, firstActivity));
        }

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var session = await ctx.Set<ChatSession>().FirstAsync(c => c.Id == chatId);
            session.ProductiveSilenceFiredAt.ShouldNotBeNull();

            // Second invocation: ProductiveSilenceFiredAt is now set → handler noops.
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new ProductiveSilenceCheck(chatId, firstActivity));

            var step1Rows = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.DidYouFind)
                .ToListAsync();
            step1Rows.ShouldHaveSingleItem(); // no second insert
        }
    }

    [Fact]
    public async Task ProductiveSilence_BelowMinMessages_DoesNotFire()
    {
        const string clientPhone = "+22070008007";
        const string nonConsentingProvider = "+2203000002";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 2", timeout: TimeSpan.FromSeconds(20));

        await http.ExpectOutboundAsync(
            clientPhone,
            m => m.Body.Contains("private chat", StringComparison.OrdinalIgnoreCase) ||
                 m.Body.Contains("chat is ready", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        Guid chatId;
        Guid clientParticipantId;
        Guid providerParticipantId;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var match = await ctx.Set<Features.Matching.MatchAggregate.Match>()
                .FirstAsync(m => m.ProviderPhone == nonConsentingProvider && m.ChatId != null);
            chatId = match.ChatId!.Value;
            clientParticipantId = (await ctx.Set<Features.ChatSession.ParticipantAggregate.ChatParticipant>()
                .FirstAsync(p => p.ChatId == chatId
                              && p.Role == Features.ChatSession.ParticipantAggregate.ChatParticipantRole.Client)).Id;
            providerParticipantId = (await ctx.Set<Features.ChatSession.ParticipantAggregate.ChatParticipant>()
                .FirstAsync(p => p.ChatId == chatId
                              && p.Role == Features.ChatSession.ParticipantAggregate.ChatParticipantRole.Provider)).Id;
        }

        // Only 2 per side — below default ProductiveSilenceMinMessagesPerSide=3.
        await SeedChatMessagesAsync(chatId, clientParticipantId, providerParticipantId, 2);
        var firstActivity = await GetLastActivityAsync(chatId);

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new ProductiveSilenceCheck(chatId, firstActivity));

            var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var step1Rows = await ctx.Set<MatchFeedback>()
                .Where(f => f.Step == FeedbackStep.DidYouFind)
                .ToListAsync();
            step1Rows.ShouldBeEmpty();
            var session = await ctx.Set<ChatSession>().FirstAsync(c => c.Id == chatId);
            session.ProductiveSilenceFiredAt.ShouldBeNull();
        }
    }

    private async Task SeedChatMessagesAsync(
        Guid chatId,
        Guid clientParticipantId,
        Guid providerParticipantId,
        int perSide)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var session = await ctx.Set<ChatSession>().FirstAsync(c => c.Id == chatId);
        var now = DateTimeOffset.UtcNow;
        long seqClient = 0;
        long seqProvider = 0;
        for (var i = 0; i < perSide; i++)
        {
            var pubClient = Features.ChatSession.SessionAggregate.ChatMessage.Create(
                Guid.CreateVersion7(), chatId, clientParticipantId, ++seqClient,
                new byte[20], new byte[12], now);
            var pubProvider = Features.ChatSession.SessionAggregate.ChatMessage.Create(
                Guid.CreateVersion7(), chatId, providerParticipantId, ++seqProvider,
                new byte[20], new byte[12], now);
            ctx.Add(pubClient);
            ctx.Add(pubProvider);
        }
        // Touch session so LastActivityAt sits at the seed point — handler checks
        // ScheduledForActivityAt against this and noops if a fresher activity exists.
        session.Touch(now);
        await ctx.SaveChangesAsync();
    }

    private async Task<DateTimeOffset> GetLastActivityAsync(Guid chatId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        return (await ctx.Set<ChatSession>().FirstAsync(c => c.Id == chatId)).LastActivityAt;
    }
}
