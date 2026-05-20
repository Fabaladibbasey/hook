using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.PresentMatches;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Matching;

public class MatchPresenterFactsShapeTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IWhatsappClient> _whatsappMock = new();
    private readonly List<SentMessage> _sent = [];
    private IReadOnlyDictionary<string, string>? _capturedFacts;

    public MatchPresenterFactsShapeTests()
    {
        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplyContext ctx, CancellationToken _) =>
            {
                _capturedFacts = ctx.Facts;
                return "ok";
            });

        _whatsappMock.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PhoneNumber, string, CancellationToken>((to, body, _) => _sent.Add(new SentMessage(to, body)))
            .ReturnsAsync(string.Empty);
    }

    [Fact]
    public async Task Facts_matches_value_is_json_array_of_all_scored_items()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 8);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        _capturedFacts.ShouldNotBeNull();
        var matchesFact = _capturedFacts!["matches"];

        using var doc = JsonDocument.Parse(matchesFact);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(8);

        var first = doc.RootElement[0];
        first.GetProperty("n").GetInt32().ShouldBe(1);
        first.GetProperty("phone").GetString().ShouldNotBeNullOrWhiteSpace();
        first.TryGetProperty("distance", out _).ShouldBeTrue();
        first.TryGetProperty("score", out _).ShouldBeTrue();

        _capturedFacts!["count"].ShouldBe("8");
        _capturedFacts!["service"].ShouldBe("plumbing");
    }

    [Fact]
    public async Task Facts_matches_preserves_count_when_below_cap()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 3);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "carpentry");

        _capturedFacts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task PresentAsync_BatchExceedsCap_TruncatesToCap()
    {
        var presenter = Build(cap: 3);

        var batch = MakeBatchWith(scoredCount: 8);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        _capturedFacts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Empty_batch_falls_back_to_no_providers_text_and_does_not_call_ai()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 0);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        _capturedFacts.ShouldBeNull();   // AI never invoked
        _sent.Count.ShouldBe(1);
        _sent[0].Body.ShouldContain("No providers found");
    }

    [Fact]
    public async Task SinglePresented_ActionLineMentionsPickAndNew()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 1);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        var body = _sent.Single().Body;
        body.ShouldContain("PICK 1");
        body.ShouldContain("NEW", Case.Sensitive);
        body.ShouldNotContain("share contact", Case.Insensitive);
    }

    [Fact]
    public async Task MultiplePresented_ActionLineMentionsCommaAndAll()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 4);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        var body = _sent.Single().Body;
        body.ShouldContain("PICK 1,2");
        body.ShouldContain("PICK ALL");
        body.ShouldNotContain("Reply PICK 1 to PICK 4 to share", Case.Insensitive);
    }

    [Fact]
    public async Task Facts_label_is_Related_for_cross_level_matches()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 2, kind: Features.Matching.MatchAggregate.MatchKind.Broadened);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            item.GetProperty("label").GetString().ShouldBe("Related");
        }
    }

    [Fact]
    public async Task Facts_label_is_empty_for_exact_matches()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 2, kind: Features.Matching.MatchAggregate.MatchKind.Exact);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            item.GetProperty("label").GetString().ShouldBe("");
        }
    }

    [Fact]
    public async Task PresentAsync_DoesNotNotifyAnyProvider_EvenWhenManyMatches()
    {
        var presenter = Build(cap: 100);

        var batch = MakeBatchWith(scoredCount: 5);
        await presenter.PresentAsync(PhoneNumber.Parse("+15550000001"), batch, "plumbing");

        // Privacy invariant: presentation only addresses the requester, never any provider phone.
        _sent.Count.ShouldBe(1);
        _sent[0].To.Value.ShouldBe("+15550000001");
    }

    private MatchPresenter Build(int cap) =>
        new(
            ai: _aiMock.Object,
            whatsapp: _whatsappMock.Object,
            options: Options.Create(new MatchingOptions { TopMatchesPerBatch = cap }),
            logger: NullLogger<MatchPresenter>.Instance);

    private static MatchBatch MakeBatchWith(int scoredCount, Hook.Features.Matching.MatchAggregate.MatchKind kind = Features.Matching.MatchAggregate.MatchKind.Exact)
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
                Score: 1.0 - (i * 0.05),
                Kind: kind))
            .ToList();
        return new MatchBatch(Guid.NewGuid(), [], scored);
    }

    private sealed record SentMessage(PhoneNumber To, string Body);
}
