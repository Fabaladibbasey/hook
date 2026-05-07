using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Feedback;

public class Step1FeedbackHandlerTests
{
    [Fact]
    public async Task Handle_HappyPath_AddsPendingThenSends()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        var handler = deps.Build();

        await handler.Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Single(deps.Feedback.Added);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Equal(new[] { "TryAdd", "Send" }, deps.CallOrder);
        Assert.Empty(deps.Feedback.Deleted);
    }

    [Fact]
    public async Task Handle_ExistingPendingRowRacesViaUniqueIndex_NoOp()
    {
        // Simulates the 23505 catch path inside TryAddPendingAsync — i.e., a previous
        // Step1 prompt for this match left a Pending row, so the partial unique index
        // rejects this insert and the handler must exit before sending.
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Feedback.TryAddResult = false;
        var handler = deps.Build();

        await handler.Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(deps.Feedback.Added);
        Assert.Empty(deps.Whatsapp.Sent);
    }

    [Fact]
    public async Task Handle_TryAddPendingFails_NoSend()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Feedback.TryAddResult = false;
        var handler = deps.Build();

        await handler.Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Equal(new[] { "TryAdd" }, deps.CallOrder);
    }

    [Fact]
    public async Task Handle_WhatsappSendThrows_DeletesPendingAndRethrows()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Whatsapp.ThrowOnSend = new InvalidOperationException("transport down");
        var handler = deps.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new Step1FeedbackCheck(match.Id), CancellationToken.None));

        Assert.Single(deps.Feedback.Added);
        Assert.Single(deps.Feedback.Deleted);
        Assert.Equal(deps.Feedback.Added[0].Id, deps.Feedback.Deleted[0]);
    }

    private static Match SeedMatch(Deps deps)
    {
        var requestId = Guid.NewGuid();
        var match = new Match
        {
            RequestId = requestId,
            ProviderPhone = "+2203331234",
            ServiceSlug = "plumbing"
        };
        deps.Matches.Stored[match.Id] = match;

        deps.Requests.Stored[requestId] = ServiceRequestEntity.Create(
            "+2203339999", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, false);
        return match;
    }

    private sealed class Deps
    {
        public FakeFeedbackRepository Feedback { get; }
        public FakeMatchRepository Matches { get; } = new();
        public FakeRequestRepository Requests { get; } = new();
        public FakeAi Ai { get; } = new();
        public FakeWhatsapp Whatsapp { get; }
        public List<string> CallOrder { get; } = new();

        public Deps()
        {
            Feedback = new FakeFeedbackRepository(CallOrder);
            Whatsapp = new FakeWhatsapp(CallOrder);
        }

        public Step1FeedbackHandler Build() =>
            new(Matches, Requests, Feedback, Ai, Whatsapp,
                NullLogger<Step1FeedbackHandler>.Instance);
    }

    private sealed class FakeFeedbackRepository(List<string> callOrder) : IFeedbackRepository
    {
        public Dictionary<(Guid, FeedbackStep), MatchFeedback> PendingForMatch { get; } = new();
        public List<MatchFeedback> Added { get; } = new();
        public List<Guid> Deleted { get; } = new();
        public bool TryAddResult { get; set; } = true;

        public Task<MatchFeedback?> GetLatestPendingForClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);

        public Task<MatchFeedback?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);

        public Task AddAsync(MatchFeedback feedback, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ProviderStats?> GetStatsAsync(string providerPhone, CancellationToken ct = default) =>
            Task.FromResult<ProviderStats?>(null);

        public Task UpsertStatsAsync(ProviderStats stats, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteStatsAsync(string providerPhone, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<MatchFeedback?> GetPendingAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default) =>
            Task.FromResult(PendingForMatch.TryGetValue((matchId, step), out var f) ? f : null);

        public Task<MatchFeedback?> GetLatestByMatchAndStepAsync(Guid matchId, FeedbackStep step, CancellationToken ct = default) =>
            Task.FromResult<MatchFeedback?>(null);

        public Task<bool> TryClaimPendingAsync(Guid feedbackId, FeedbackAnswer answer, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> TryAddPendingAsync(MatchFeedback feedback, CancellationToken ct = default)
        {
            callOrder.Add("TryAdd");
            if (TryAddResult) Added.Add(feedback);
            return Task.FromResult(TryAddResult);
        }

        public Task<bool> DeletePendingAsync(Guid feedbackId, CancellationToken ct = default)
        {
            Deleted.Add(feedbackId);
            return Task.FromResult(true);
        }
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
        public Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default) =>
            Task.FromResult(true);
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
        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) =>
            Task.FromResult(new ServiceJudgeResult(null, true, proposedSlug));
        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default) =>
            Task.FromResult("ok");
        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new LanguageDetectionResult("en", 1.0));
    }

    private sealed class FakeWhatsapp(List<string> callOrder) : IWhatsappClient
    {
        public List<(PhoneNumber To, string Body)> Sent { get; } = new();
        public Exception? ThrowOnSend { get; set; }

        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            callOrder.Add("Send");
            if (ThrowOnSend is not null) throw ThrowOnSend;
            Sent.Add((to, body));
            return Task.FromResult("msg-1");
        }
    }
}
