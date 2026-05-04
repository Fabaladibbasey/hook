using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Matching.PresentMatches;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using ProviderAvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.Matching;

public class MatchPresenterFactsShapeTests
{
    [Fact]
    public async Task Facts_matches_value_is_json_array_capped_at_5_when_more_scored()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture);

        var batch = MakeBatchWith(scoredCount: 8);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        capture.Facts.ShouldNotBeNull();
        var matchesFact = capture.Facts!["matches"];

        using var doc = JsonDocument.Parse(matchesFact);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(5);

        var first = doc.RootElement[0];
        first.GetProperty("n").GetInt32().ShouldBe(1);
        first.GetProperty("phone").GetString().ShouldNotBeNullOrWhiteSpace();
        first.TryGetProperty("distance", out _).ShouldBeTrue();
        first.TryGetProperty("score", out _).ShouldBeTrue();

        capture.Facts!["count"].ShouldBe("5");
        capture.Facts!["service"].ShouldBe("plumbing");
    }

    [Fact]
    public async Task Facts_matches_preserves_count_when_below_cap()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture);

        var batch = MakeBatchWith(scoredCount: 3);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "carpentry");

        capture.Facts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(capture.Facts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Empty_batch_falls_back_to_no_providers_text_and_does_not_call_ai()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture);

        var batch = MakeBatchWith(scoredCount: 0);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        capture.Facts.ShouldBeNull();   // AI never invoked
        capture.SentText.ShouldNotBeNull();
        capture.SentText!.ShouldContain("No providers found");
    }

    private static MatchPresenter Build(ReplyCapture capture) =>
        new(
            ai:        new CapturingAi(capture),
            whatsapp:  new CapturingWhatsapp(capture),
            matches:   new EmptyMatchRepo(),
            providers: new EmptyProviderRepo(),
            bus:       new NoopBus(),
            logger:    NullLogger<MatchPresenter>.Instance);

    private static MatchBatch MakeBatchWith(int scoredCount)
    {
        var scored = Enumerable.Range(0, scoredCount)
            .Select(i => new ScoredCandidate(
                new ProviderCandidate(
                    Phone: $"+1555000{i:D4}",
                    ShareContact: true,
                    LastActiveAt: DateTimeOffset.UtcNow,
                    DistanceKm: i + 1.234,
                    CompletedJobs: 5,
                    SuccessRate: 0.9 - (i * 0.01)),
                Score: 1.0 - (i * 0.05)))
            .ToList();
        return new MatchBatch(Guid.NewGuid(), Array.Empty<Match>(), scored);
    }

    private sealed class ReplyCapture
    {
        public IReadOnlyDictionary<string, string>? Facts;
        public string? SentText;
    }

    private sealed class CapturingAi(ReplyCapture capture) : IConversationAi
    {
        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new IntentDetectionResult(IntentKind.Unknown, 0, "en", null));
        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new ServiceExtractionResult(Array.Empty<string>()));
        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) =>
            Task.FromResult(new ServiceJudgeResult(null, true, null));
        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default)
        {
            capture.Facts = context.Facts;
            return Task.FromResult("ok");
        }
        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new LanguageDetectionResult("en", 1));
    }

    private sealed class CapturingWhatsapp(ReplyCapture capture) : IWhatsappClient
    {
        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            capture.SentText = body;
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class EmptyMatchRepo : IMatchRepository
    {
        public Task<IReadOnlyList<Match>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Match>>(Array.Empty<Match>());
        public Task AddAsync(Match match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<Match> newMatches, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Match?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Match?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyProviderRepo : IProviderAvailabilityRepository
    {
        public Task<ProviderAvailabilityEntity?> GetAsync(string phone, CancellationToken ct = default) =>
            Task.FromResult<ProviderAvailabilityEntity?>(null);
        public Task AddAsync(ProviderAvailabilityEntity availability, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string phone, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopBus : IMessageBus
    {
        public string? TenantId { get; set; }
        public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
        public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
        public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
        public Task InvokeAsync(object message, CancellationToken ct = default, TimeSpan? timeout = null) => Task.CompletedTask;
        public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken ct = default, TimeSpan? timeout = null) => Task.CompletedTask;
        public Task<TResponse> InvokeAsync<TResponse>(object message, CancellationToken ct = default, TimeSpan? timeout = null) =>
            throw new NotImplementedException();
        public Task<TResponse> InvokeAsync<TResponse>(object message, DeliveryOptions options, CancellationToken ct = default, TimeSpan? timeout = null) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken ct = default, TimeSpan? timeout = null) =>
            throw new NotImplementedException();
        public Task<TResponse> InvokeForTenantAsync<TResponse>(string tenantId, object message, CancellationToken ct = default, TimeSpan? timeout = null) =>
            throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotImplementedException();
        public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw new NotImplementedException();
        public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) =>
            throw new NotImplementedException();
    }
}
