using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Feedback.Step2Prompt;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step2FeedbackHandlerTests
{
    [Fact]
    public async Task Handle_PriorMissing_NoOp()
    {
        var deps = new Deps();
        var handler = deps.Build();

        await handler.Handle(new Step2FeedbackCheck(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Feedback.Added);
    }

    [Fact]
    public async Task Handle_PriorWrongStep_NoOp()
    {
        var deps = new Deps();
        var prior = new MatchFeedback { MatchId = Guid.NewGuid(), Step = FeedbackStep.JobCompleted };
        deps.Feedback.Stored[prior.Id] = prior;

        var handler = deps.Build();
        await handler.Handle(new Step2FeedbackCheck(prior.Id), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Feedback.Added);
    }

    [Fact]
    public async Task Handle_PriorAnswerNotYes_NoOp()
    {
        var deps = new Deps();
        var prior = new MatchFeedback { MatchId = Guid.NewGuid(), Step = FeedbackStep.DidYouFind };
        prior.Answer = FeedbackAnswer.No;
        deps.Feedback.Stored[prior.Id] = prior;

        var handler = deps.Build();
        await handler.Handle(new Step2FeedbackCheck(prior.Id), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
    }

    [Fact]
    public async Task Handle_MatchDeleted_NoOp()
    {
        var deps = new Deps();
        var prior = new MatchFeedback { MatchId = Guid.NewGuid(), Step = FeedbackStep.DidYouFind };
        prior.Answer = FeedbackAnswer.Yes;
        deps.Feedback.Stored[prior.Id] = prior;

        var handler = deps.Build();
        await handler.Handle(new Step2FeedbackCheck(prior.Id), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Feedback.Added);
    }

    private sealed class Deps
    {
        public FakeFeedbackRepository Feedback { get; } = new();
        public FakeMatchRepository Matches { get; } = new();
        public FakeRequestRepository Requests { get; } = new();
        public FakeAi Ai { get; } = new();
        public FakeWhatsapp Whatsapp { get; } = new();

        public Step2FeedbackHandler Build() =>
            new(Feedback, Matches, Requests, Ai, Whatsapp,
                NullLogger<Step2FeedbackHandler>.Instance);
    }

    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        public Dictionary<Guid, MatchFeedback> Stored { get; } = new();
        public List<MatchFeedback> Added { get; } = new();

        public Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);

        public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var f) ? f : null);

        public Task AddAsync(MatchFeedback feedback, CancellationToken ct = default)
        {
            Added.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
            Task.FromResult<ProviderStats?>(null);

        public Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMatchRepository : IMatchRepository
    {
        public Dictionary<Guid, Match> Stored { get; } = new();

        public Task<Match?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var m) ? m : null);
        public Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Match>>(Array.Empty<Match>());
        public Task AddAsync(Match match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRequestRepository : IServiceRequestRepository
    {
        public Dictionary<Guid, ServiceRequestEntity> Stored { get; } = new();
        public Task<ServiceRequestEntity?> GetActiveByClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<ServiceRequestEntity?>(null);
        public Task<ServiceRequestEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var r) ? r : null);
        public Task AddAsync(ServiceRequestEntity request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAi : IConversationAi
    {
        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new IntentDetectionResult(IntentKind.Unknown, 0.5, "en", "fake"));

        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new ServiceExtractionResult(Array.Empty<string>()));

        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(
            string proposedSlug,
            IReadOnlyList<string> candidateSlugs,
            CancellationToken ct = default) =>
            Task.FromResult(new ServiceJudgeResult(null, true, proposedSlug));

        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default) =>
            Task.FromResult("ok");

        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new LanguageDetectionResult("en", 1.0));
    }

    private sealed class FakeWhatsapp : IWhatsappClient
    {
        public List<(PhoneNumber To, string Body)> Sent { get; } = new();

        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            Sent.Add((to, body));
            return Task.FromResult("msg-1");
        }
    }
}
