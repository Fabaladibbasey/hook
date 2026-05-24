using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.Step2Intent;
using Hook.Shared.Persistence.Data;
using Hook.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Feedback;

[Collection("Pipeline-2")]
public sealed class Step2SmarterParserPipelineTests : PipelineTestBase
{
    public Step2SmarterParserPipelineTests(DevPipelineFixture fx) : base(fx)
    {
        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<Hook.Features.Ai.IConversationAi>();
        fake.ResetOverrides();
    }

    [Fact]
    public async Task Step2_Yes_DeterministicShortCircuit_NoAiCall_ClaimsYesAndAcks()
    {
        const string clientPhone = "+22070008101";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2Prompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<Hook.Features.Ai.IConversationAi>();
        var aiCallsBefore = fake.ExtractStep2IntentCalls;

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body == FeedbackCopy.Step2YesAck,
            since: step2Prompt.At);

        fake.ExtractStep2IntentCalls.ShouldBe(aiCallsBefore);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var step2Row = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted)
            .OrderByDescending(f => f.PromptedAt)
            .FirstAsync();
        step2Row.Answer.ShouldBe(FeedbackAnswer.Yes);
    }

    [Fact]
    public async Task Step2_InProgressWithAiEta_ClaimsEtaCapturedAndSchedules()
    {
        const string clientPhone = "+22070008102";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2Prompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        // Force AI path: a phrase the deterministic classifier misses but with
        // an override that produces InProgress + ETA.
        var fake = (FakeConversationAi)_fx.Factory.Services.GetRequiredService<Hook.Features.Ai.IConversationAi>();
        var eta = DateTimeOffset.UtcNow.AddHours(2);
        fake.OverrideStep2Intent("call you soon", new Step2ParseResult(Step2ReplyIntent.InProgress, eta));

        await _fx.InjectTextAndAwaitAsync(clientPhone, "call you soon");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("check back", StringComparison.OrdinalIgnoreCase),
            since: step2Prompt.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var step2Row = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted)
            .OrderByDescending(f => f.PromptedAt)
            .FirstAsync();
        step2Row.Answer.ShouldBe(FeedbackAnswer.EtaCaptured);
        step2Row.EtaUtc.ShouldNotBeNull();

        // No AwaitingEta row — single-shot InProgress+ETA bypasses it.
        var awaitingRows = await ctx.Set<MatchFeedback>()
            .Where(f => f.MatchId == step2Row.MatchId && f.Step == FeedbackStep.AwaitingEta)
            .ToListAsync();
        awaitingRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Step2_No_ClaimsNoAndAcks()
    {
        const string clientPhone = "+22070008103";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2Prompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "no");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body == FeedbackCopy.Step2NoAck,
            since: step2Prompt.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var step2Row = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted)
            .OrderByDescending(f => f.PromptedAt)
            .FirstAsync();
        step2Row.Answer.ShouldBe(FeedbackAnswer.No);
    }

    [Fact]
    public async Task Step2_StopAsking_ClaimsSkippedAndAcksWithStopCopy()
    {
        const string clientPhone = "+22070008104";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2Prompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "stop asking me");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body == FeedbackCopy.StopAck,
            since: step2Prompt.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var step2Row = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted)
            .OrderByDescending(f => f.PromptedAt)
            .FirstAsync();
        step2Row.Answer.ShouldBe(FeedbackAnswer.Skipped);
    }

    [Fact]
    public async Task Step2_InProgressNoEta_ReservesAwaitingEtaAndAsks()
    {
        const string clientPhone = "+22070008105";
        using var http = _fx.Factory.CreateClient();

        var presented = await MatchPipelineHelpers.ReachInitialPresentAsync(
            _fx, clientPhone, sharePhoneConsent: true);
        await _fx.InjectTextAndAwaitAsync(clientPhone, "PICK 1", timeout: TimeSpan.FromSeconds(20));
        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-1-did-you-find", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "yes");
        var step2Prompt = await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body.Contains("feedback-step-2-job-completed", StringComparison.OrdinalIgnoreCase),
            since: presented.At);

        await _fx.InjectTextAndAwaitAsync(clientPhone, "in progress");

        await http.WaitForOutboundAsync(
            clientPhone,
            m => m.Body == FeedbackCopy.Step2AwaitingEtaAsk,
            since: step2Prompt.At);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var step2Row = await ctx.Set<MatchFeedback>()
            .Where(f => f.Step == FeedbackStep.JobCompleted)
            .OrderByDescending(f => f.PromptedAt)
            .FirstAsync();
        step2Row.Answer.ShouldBe(FeedbackAnswer.InProgress);

        var awaiting = await ctx.Set<MatchFeedback>()
            .Where(f => f.MatchId == step2Row.MatchId && f.Step == FeedbackStep.AwaitingEta)
            .ToListAsync();
        awaiting.ShouldHaveSingleItem();
        awaiting[0].Answer.ShouldBe(FeedbackAnswer.Pending);
    }
}
