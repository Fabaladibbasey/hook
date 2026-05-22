using System.Collections.Frozen;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceTaxonomy;

public class SlugResolverTests
{
    private readonly Dictionary<string, Service> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mock<IServiceRepository> _repoMock = new();
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly List<object> _published = new();
    private Func<string, string, double> _similarityRule = (_, _) => 0;

    public SlugResolverTests()
    {
        _repoMock.Setup(x => x.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string slug, CancellationToken _) =>
                _store.TryGetValue(slug, out var s) ? s : null);

        _repoMock.Setup(x => x.FindSimilarAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string slug, int take, CancellationToken _) =>
                _store.Keys
                    .Select(s => new SlugSimilarity(s, _similarityRule(s, slug)))
                    .Where(x => x.Similarity > 0)
                    .OrderByDescending(x => x.Similarity)
                    .Take(take)
                    .ToList());

        _repoMock.Setup(x => x.AddAsync(It.IsAny<Service>(), It.IsAny<CancellationToken>()))
            .Callback<Service, CancellationToken>((s, _) => _store[s.Slug] = s)
            .Returns(Task.CompletedTask);

        // Default AI: do not auto-merge — judges every mid-band call as new.
        _aiMock.Setup(x => x.JudgeServiceMatchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string proposed, IReadOnlyList<string> _, CancellationToken __) =>
                new ServiceJudgeResult(string.Empty, true, proposed));

        _busMock.Setup(x => x.PublishAsync(
                It.IsAny<object>(),
                It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((msg, _) => _published.Add(msg))
            .Returns(ValueTask.CompletedTask);
    }

    private SlugResolver Build(double autoMerge = 0.85, double aiJudge = 0.50)
    {
        var options = Options.Create(new ServiceTaxonomyOptions
        {
            AutoMergeThreshold = autoMerge,
            AiJudgeThreshold = aiJudge
        });
        return new SlugResolver(
            _repoMock.Object,
            _aiMock.Object,
            _busMock.Object,
            options,
            NullLogger<SlugResolver>.Instance);
    }

    private void Seed(string slug) => _store[slug] = Service.Create(slug);

    [Theory]
    [InlineData("Plumbing Services!", "plumbing-services")]
    [InlineData("computer_repair", "computer-repair")]
    [InlineData("  Door / Wood Work  ", "door-wood-work")]
    [InlineData("dental--/--care", "dental-care")]
    public void Normalize_ShouldProduceKebabCaseSlug(string input, string expected)
    {
        SlugResolver.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("кардиолог", "")]                  // Cyrillic letters dropped
    [InlineData("医生", "")]                         // CJK dropped
    [InlineData("café-au-lait", "caf-au-lait")]    // é dropped, ASCII preserved
    [InlineData("MIX-кир-ascii", "mix-ascii")]     // mid-Cyrillic stripped
    public void Normalize_DropsNonAsciiHomoglyphs(string input, string expected)
    {
        SlugResolver.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnExistingWhenExactMatch()
    {
        Seed("plumbing");
        var resolver = Build();

        var result = await resolver.ResolveAsync("Plumbing", "I need a plumber");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.ReturnedExisting);
        _store["plumbing"].RawExamples.ShouldContain("I need a plumber");
    }

    [Fact]
    public async Task ResolveAsync_ShouldAutoMergeWhenSimilarityAboveAutoMergeThreshold()
    {
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.92 : 0;
        var resolver = Build();

        var result = await resolver.ResolveAsync("plumbings");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AutoMerged);
        result.TopSimilarity.ShouldBe(0.92);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseAiJudgeForMidBandSimilarity()
    {
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        _aiMock.Setup(x => x.JudgeServiceMatchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceJudgeResult("plumbing", false, string.Empty));
        var resolver = Build();

        var result = await resolver.ResolveAsync("pipe-repair", "my sink leaks");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedMerge);
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateNewSlugWhenAiJudgesNew()
    {
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.55 : 0;
        _aiMock.Setup(x => x.JudgeServiceMatchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceJudgeResult(string.Empty, true, "astrology"));
        var resolver = Build();

        var result = await resolver.ResolveAsync("astrology");

        result.CanonicalSlug.ShouldBe("astrology");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedNew);
        _store.ContainsKey("astrology").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateNewWhenSimilarityBelowAiJudgeThreshold()
    {
        Seed("plumbing");
        _similarityRule = (_, _) => 0.10;
        var resolver = Build();

        var result = await resolver.ResolveAsync("astrology");

        result.CanonicalSlug.ShouldBe("astrology");
        result.Resolution.ShouldBe(SlugResolution.Created);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectAiJudgedMatchOutsideCandidateList()
    {
        // Defense-in-depth: even if the IConversationAi implementation returns a slug
        // that wasn't in the candidate list, SlugResolver itself must refuse to write
        // it as a canonical row.
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        _aiMock.Setup(x => x.JudgeServiceMatchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceJudgeResult("evil-slug", false, string.Empty));
        var resolver = Build();

        var result = await resolver.ResolveAsync("plumbingg", "I do plumbingg");

        result.CanonicalSlug.ShouldNotBe("evil-slug");
        result.CanonicalSlug.ShouldBe("plumbingg");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedNew);
        _store.ContainsKey("evil-slug").ShouldBeFalse();
        _store.ContainsKey("plumbingg").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ShouldHonourAiJudgedMatchWhenInCandidateList()
    {
        Seed("plumbing");
        Seed("carpentry");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        _aiMock.Setup(x => x.JudgeServiceMatchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceJudgeResult("plumbing", false, string.Empty));
        var resolver = Build();

        var result = await resolver.ResolveAsync("pipe-fix", "leaky pipe");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedMerge);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPublishJudgeParentRequest_OnCreate()
    {
        var resolver = Build();

        await resolver.ResolveAsync("astrology", "horoscope");

        _published.OfType<JudgeParentSlugRequested>()
            .ShouldContain(r => r.Slug == "astrology");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotPublishJudgeParentRequest_OnReturnedExisting()
    {
        Seed("plumbing");
        var resolver = Build();

        await resolver.ResolveAsync("plumbing", "I need a plumber");

        _published.OfType<JudgeParentSlugRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotPublishJudgeParentRequest_OnAutoMerged()
    {
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.92 : 0;
        var resolver = Build();

        await resolver.ResolveAsync("plumbings");

        _published.OfType<JudgeParentSlugRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task AcceptExisting_PublishesJudgeParent_WhenSlugRowMissing()
    {
        // FindSimilar surfaces a slug at AutoMerge similarity, but GetBySlugAsync
        // for that slug returns null (row was deleted between read and merge —
        // possible in concurrent retention sweeps). AcceptExistingAsync re-creates
        // the row and must publish parent-judgment for the new aggregate.
        _similarityRule = (existing, _) => existing == "ghost" ? 0.92 : 0;
        _store["ghost"] = Service.Create("ghost"); // FindSimilar will see it...
        _repoMock.Setup(x => x.GetBySlugAsync("ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Service?)null); // ...but GetBySlugAsync says it's gone

        var resolver = Build();
        await resolver.ResolveAsync("ghosts");

        _published.OfType<JudgeParentSlugRequested>()
            .ShouldContain(r => r.Slug == "ghost");
    }

    [Fact]
    public async Task ResolveCoreAsync_PeerInAutoMergeBand_NotMerged_CreatesInstead()
    {
        // Filter must drop a sibling slug that committed first on its own connection.
        // Without the filter the peer would dominate FindSimilar (0.92 >= 0.85) and the
        // loser would auto-merge into it; with the filter the loser creates its own row.
        Seed("trigsim-x-abc12345");
        _similarityRule = (existing, _) =>
            existing == "trigsim-x-abc12345" ? 0.92 : 0;
        var resolver = Build();

        var peers = new HashSet<string>(StringComparer.Ordinal) { "trigsim-x-abc12345" };
        var (result, publishSlug) = await resolver.ResolveCoreAsync(
            "trigsim-y-abc12345", rawExample: "test", batchPeers: peers, CancellationToken.None);

        result.CanonicalSlug.ShouldBe("trigsim-y-abc12345");
        result.Resolution.ShouldBe(SlugResolution.Created);
        publishSlug.ShouldBe("trigsim-y-abc12345");
    }

    [Fact]
    public async Task ResolveCoreAsync_PeerSetEmpty_NoFiltering_AutoMergesAsBefore()
    {
        // Baseline: when batchPeers is empty the resolver behaves identically to ResolveAsync.
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.92 : 0;
        var resolver = Build();

        var (result, _) = await resolver.ResolveCoreAsync(
            "plumbings", rawExample: "test", batchPeers: FrozenSet<string>.Empty, CancellationToken.None);

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AutoMerged);
    }

    [Fact]
    public async Task ResolveCoreAsync_NonPeerAtAutoMergeBand_StillMerges()
    {
        // Filter must NOT touch non-peers — cross-batch auto-merge stays intact.
        Seed("plumbing");
        _similarityRule = (existing, _) => existing == "plumbing" ? 0.92 : 0;
        var resolver = Build();

        var peers = new HashSet<string>(StringComparer.Ordinal) { "trigsim-x-abc12345" };
        var (result, _) = await resolver.ResolveCoreAsync(
            "plumbings", rawExample: "test", batchPeers: peers, CancellationToken.None);

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AutoMerged);
    }

    [Fact]
    public async Task ResolveBatchAsync_DedupesByNormalizedValue()
    {
        // "Plumbing", "plumbing-" both normalize to "plumbing". A naive batch would race
        // the PK insert; the boundary dedupe collapses them to a single resolution.
        var resolver = Build();

        var results = await resolver.ResolveBatchAsync(new[] { "Plumbing", "plumbing-" });

        results.Count.ShouldBe(1);
        results[0].CanonicalSlug.ShouldBe("plumbing");
        _store.ContainsKey("plumbing").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveBatchAsync_AllEmptyAfterNormalize_ReturnsEmpty()
    {
        // Cyrillic + CJK both normalize to empty; the boundary filters them so we don't
        // throw partway through Task.WhenAll and lose sibling resolutions.
        var resolver = Build();

        var results = await resolver.ResolveBatchAsync(new[] { "кардиолог", "医生" });

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveBatchAsync_OverMaxBatchSize_Throws()
    {
        var resolver = Build();
        var slugs = Enumerable.Range(0, 17).Select(i => $"slug-{i}").ToArray();

        await Should.ThrowAsync<ArgumentException>(() => resolver.ResolveBatchAsync(slugs));
    }

    [Theory]
    [InlineData("very-long-input-that-exceeds-the-sixty-four-character-cap-by-a-comfortable-margin-and-then-some", 64)]
    public void Normalize_TruncatesAtMaxSlugLength(string input, int max)
    {
        var result = SlugResolver.Normalize(input);
        result.Length.ShouldBeLessThanOrEqualTo(max);
    }
}
