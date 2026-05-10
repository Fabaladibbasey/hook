using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Matching.PresentMatches;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.Matching;

public class MatchPresenterFactsShapeTests
{
    [Fact]
    public async Task Facts_matches_value_is_json_array_of_all_scored_items()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 8);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        capture.Facts.ShouldNotBeNull();
        var matchesFact = capture.Facts!["matches"];

        using var doc = JsonDocument.Parse(matchesFact);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(8);

        var first = doc.RootElement[0];
        first.GetProperty("n").GetInt32().ShouldBe(1);
        first.GetProperty("phone").GetString().ShouldNotBeNullOrWhiteSpace();
        first.TryGetProperty("distance", out _).ShouldBeTrue();
        first.TryGetProperty("score", out _).ShouldBeTrue();

        capture.Facts!["count"].ShouldBe("8");
        capture.Facts!["service"].ShouldBe("plumbing");
    }

    [Fact]
    public async Task Facts_matches_preserves_count_when_below_cap()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 3);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "carpentry");

        capture.Facts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(capture.Facts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task PresentAsync_BatchExceedsCap_TruncatesToCap()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 3);

        var batch = MakeBatchWith(scoredCount: 8);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        capture.Facts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(capture.Facts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Empty_batch_falls_back_to_no_providers_text_and_does_not_call_ai()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 0);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        capture.Facts.ShouldBeNull();   // AI never invoked
        capture.SentMessages.Count.ShouldBe(1);
        capture.SentMessages[0].Body.ShouldContain("No providers found");
    }

    [Fact]
    public async Task SinglePresented_ActionLineMentionsPickAndNew()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 1);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        var body = capture.SentMessages.Single().Body;
        body.ShouldContain("PICK 1");
        body.ShouldContain("NEW", Case.Sensitive);
        body.ShouldNotContain("share contact", Case.Insensitive);
    }

    [Fact]
    public async Task MultiplePresented_ActionLineMentionsCommaAndAll()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 4);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        var body = capture.SentMessages.Single().Body;
        body.ShouldContain("PICK 1,2");
        body.ShouldContain("PICK ALL");
        body.ShouldNotContain("Reply PICK 1 to PICK 4 to share", Case.Insensitive);
    }

    [Fact]
    public async Task PresentAsync_DoesNotNotifyAnyProvider_EvenWhenManyMatches()
    {
        var capture = new ReplyCapture();
        var presenter = Build(capture, cap: 100);

        var batch = MakeBatchWith(scoredCount: 5);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        // Privacy invariant: presentation only addresses the requester, never any provider phone.
        capture.SentMessages.Count.ShouldBe(1);
        capture.SentMessages[0].To.Value.ShouldBe("+15550000001");
    }

    private static MatchPresenter Build(ReplyCapture capture, int cap) =>
        new(
            ai: new CapturingAi(capture),
            whatsapp: new CapturingWhatsapp(capture),
            options: Options.Create(new MatchingOptions { TopMatchesPerBatch = cap }),
            logger: NullLogger<MatchPresenter>.Instance);

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

    private sealed record SentMessage(PhoneNumber To, string Body);

    private sealed class ReplyCapture
    {
        public IReadOnlyDictionary<string, string>? Facts;
        public List<SentMessage> SentMessages { get; } = new();
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
        public Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class CapturingWhatsapp(ReplyCapture capture) : IWhatsappClient
    {
        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            capture.SentMessages.Add(new SentMessage(to, body));
            return Task.FromResult(string.Empty);
        }
    }
}
