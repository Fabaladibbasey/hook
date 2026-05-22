using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Matching.PresentMatches;
using Hook.Features.Matching.PresentMatches.Dispatch;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.UnitTests.Matching;

public class MatchPresenterFactsShapeTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];
    private IReadOnlyDictionary<string, string>? _capturedFacts;

    public MatchPresenterFactsShapeTests()
    {
        _aiMock.Setup(x => x.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplyContext ctx, CancellationToken _) =>
            {
                _capturedFacts = ctx.Facts;
                return "ok";
            });

        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextRequested)msg))
            .Returns(ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Facts_matches_value_is_json_array_of_all_scored_items()
    {
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 8), CancellationToken.None);

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
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 3, slug: "carpentry"), CancellationToken.None);

        _capturedFacts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Handle_BatchExceedsCap_TruncatesToCap()
    {
        await BuildHandler(cap: 3).Handle(BuildRequest(count: 8), CancellationToken.None);

        _capturedFacts!["count"].ShouldBe("3");
        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        doc.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Empty_batch_falls_back_to_no_providers_text_and_does_not_call_ai()
    {
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 0), CancellationToken.None);

        _capturedFacts.ShouldBeNull();
        _sent.Count.ShouldBe(1);
        _sent[0].Text.ShouldContain("No providers found");
    }

    [Fact]
    public async Task SinglePresented_ActionLineMentionsPickAndNew()
    {
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 1), CancellationToken.None);

        var body = _sent.Single().Text;
        body.ShouldContain("PICK 1");
        body.ShouldContain("NEW", Case.Sensitive);
        body.ShouldNotContain("share contact", Case.Insensitive);
    }

    [Fact]
    public async Task MultiplePresented_ActionLineMentionsCommaAndAll()
    {
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 4), CancellationToken.None);

        var body = _sent.Single().Text;
        body.ShouldContain("PICK 1,2");
        body.ShouldContain("PICK ALL");
        body.ShouldNotContain("Reply PICK 1 to PICK 4 to share", Case.Insensitive);
    }

    [Fact]
    public async Task Facts_label_is_Related_for_cross_level_matches()
    {
        await BuildHandler(cap: 100).Handle(
            BuildRequest(count: 2, kind: MatchKind.Broadened),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            item.GetProperty("label").GetString().ShouldBe("Related");
        }
    }

    [Fact]
    public async Task Facts_label_is_empty_for_exact_matches()
    {
        await BuildHandler(cap: 100).Handle(
            BuildRequest(count: 2, kind: MatchKind.Exact),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(_capturedFacts!["matches"]);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            item.GetProperty("label").GetString().ShouldBe("");
        }
    }

    [Fact]
    public async Task Handle_DoesNotNotifyAnyProvider_EvenWhenManyMatches()
    {
        await BuildHandler(cap: 100).Handle(BuildRequest(count: 5), CancellationToken.None);

        _sent.Count.ShouldBe(1);
        _sent[0].To.Value.ShouldBe("+220300001");
    }

    [Fact]
    public async Task MatchPresenter_NonEmpty_PublishesPresentMatchesRequested_WithDtos()
    {
        var batchMatches = Enumerable.Range(0, 3)
            .Select(i => MatchEntity.Create(Guid.NewGuid(), $"+220300{i:D4}", "plumbing", 1, 0.5, DateTimeOffset.UtcNow))
            .ToList();
        var batch = new MatchBatch(Guid.NewGuid(), batchMatches, Scored(3, MatchKind.Exact));

        PresentMatchesRequested? captured = null;
        _busMock.Setup(x => x.PublishAsync(It.IsAny<PresentMatchesRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => captured = (PresentMatchesRequested)m)
            .Returns(ValueTask.CompletedTask);

        await new MatchPresenter(_busMock.Object).PresentAsync(
            PhoneNumber.Parse("+220300001"), batch, "plumbing");

        captured.ShouldNotBeNull();
        captured!.RequestId.ShouldBe(batch.RequestId);
        captured.ServiceSlug.ShouldBe("plumbing");
        captured.ClientPhone.Value.ShouldBe("+220300001");
        captured.Matches.Count.ShouldBe(3);
        captured.Matches[0].ProviderPhone.ShouldBe("+2203000000");
    }

    [Fact]
    public async Task MatchPresenter_Empty_PublishesNoProvidersText_NotPresentMatchesRequested()
    {
        var batch = new MatchBatch(Guid.NewGuid(), [], []);

        await new MatchPresenter(_busMock.Object).PresentAsync(
            PhoneNumber.Parse("+220300001"), batch, "plumbing");

        _sent.Count.ShouldBe(1);
        _sent[0].Text.ShouldContain("No providers found");
        _busMock.Verify(b => b.PublishAsync(
            It.IsAny<PresentMatchesRequested>(), It.IsAny<DeliveryOptions>()), Times.Never);
    }

    private PresentMatchesHandler BuildHandler(int cap) =>
        new(
            _aiMock.Object,
            _busMock.Object,
            Options.Create(new MatchingOptions { TopMatchesPerBatch = cap }),
            NullLogger<PresentMatchesHandler>.Instance);

    private static PresentMatchesRequested BuildRequest(
        int count,
        string slug = "plumbing",
        MatchKind kind = MatchKind.Exact)
    {
        var dtos = Enumerable.Range(0, count)
            .Select(i => new MatchPresentationDto(
                ProviderPhone: $"+220300{i:D4}",
                DistanceKm: i + 1.234,
                Score: 1.0 - (i * 0.05),
                Kind: kind))
            .ToList();
        return new PresentMatchesRequested(
            PhoneNumber.Parse("+220300001"),
            Guid.NewGuid(),
            slug,
            dtos);
    }

    private static IReadOnlyList<ScoredCandidate> Scored(int count, MatchKind kind) =>
        Enumerable.Range(0, count)
            .Select(i => new ScoredCandidate(
                new ProviderCandidate(
                    Phone: $"+220300{i:D4}",
                    ShareContact: true,
                    LastActiveAt: DateTimeOffset.UtcNow,
                    DistanceKm: i + 1.234,
                    CompletedJobs: 5,
                    SuccessRate: 0.9 - (i * 0.01)),
                Score: 1.0 - (i * 0.05),
                Kind: kind))
            .ToList();
}
