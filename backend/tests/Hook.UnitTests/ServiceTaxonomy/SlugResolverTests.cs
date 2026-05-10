using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.ServiceTaxonomy;

public class SlugResolverTests
{
    private static SlugResolver Build(
        FakeServiceRepository repository,
        IConversationAi? ai = null,
        double autoMerge = 0.85,
        double aiJudge = 0.50)
    {
        var options = Options.Create(new ServiceTaxonomyOptions
        {
            AutoMergeThreshold = autoMerge,
            AiJudgeThreshold = aiJudge
        });

        return new SlugResolver(
            repository,
            ai ?? new FakeConversationAi(),
            options,
            NullLogger<SlugResolver>.Instance);
    }

    [Theory]
    [InlineData("Plumbing Services!", "plumbing-services")]
    [InlineData("computer_repair", "computer-repair")]
    [InlineData("  Door / Wood Work  ", "door-wood-work")]
    [InlineData("dental--/--care", "dental-care")]
    public void Normalize_ShouldProduceKebabCaseSlug(string input, string expected)
    {
        SlugResolver.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnExistingWhenExactMatch()
    {
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        var resolver = Build(repo);

        var result = await resolver.ResolveAsync("Plumbing", "I need a plumber");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.ReturnedExisting);
        repo.GetSeed("plumbing")!.RawExamples.ShouldContain("I need a plumber");
    }

    [Fact]
    public async Task ResolveAsync_ShouldAutoMergeWhenSimilarityAboveAutoMergeThreshold()
    {
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.SimilarityRule = (existing, _) => existing == "plumbing" ? 0.92 : 0;
        var resolver = Build(repo);

        var result = await resolver.ResolveAsync("plumbings", null);

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AutoMerged);
        result.TopSimilarity.ShouldBe(0.92);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseAiJudgeForMidBandSimilarity()
    {
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.SimilarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        var ai = new ScriptedAi { JudgeResult = new ServiceJudgeResult("plumbing", false, null) };
        var resolver = Build(repo, ai);

        var result = await resolver.ResolveAsync("pipe-repair", "my sink leaks");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedMerge);
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateNewSlugWhenAiJudgesNew()
    {
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.SimilarityRule = (existing, _) => existing == "plumbing" ? 0.55 : 0;
        var ai = new ScriptedAi { JudgeResult = new ServiceJudgeResult(null, true, "astrology") };
        var resolver = Build(repo, ai);

        var result = await resolver.ResolveAsync("astrology", null);

        result.CanonicalSlug.ShouldBe("astrology");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedNew);
        repo.GetSeed("astrology").ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreateNewWhenSimilarityBelowAiJudgeThreshold()
    {
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.SimilarityRule = (_, _) => 0.10;
        var resolver = Build(repo);

        var result = await resolver.ResolveAsync("astrology", null);

        result.CanonicalSlug.ShouldBe("astrology");
        result.Resolution.ShouldBe(SlugResolution.Created);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectAiJudgedMatchOutsideCandidateList()
    {
        // Defense-in-depth: even if the IConversationAi implementation returns a slug
        // that wasn't in the candidate list (e.g. a future bug or a non-validating
        // adapter), SlugResolver itself must refuse to write it as a canonical row.
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.SimilarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        var ai = new ScriptedAi { JudgeResult = new ServiceJudgeResult("evil-slug", false, null) };
        var resolver = Build(repo, ai);

        var result = await resolver.ResolveAsync("plumbingg", "I do plumbingg");

        result.CanonicalSlug.ShouldNotBe("evil-slug");
        result.CanonicalSlug.ShouldBe("plumbingg");                  // user's normalized proposal
        result.Resolution.ShouldBe(SlugResolution.AiJudgedNew);
        repo.GetSeed("evil-slug").ShouldBeNull();                    // never written
        repo.GetSeed("plumbingg").ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldHonourAiJudgedMatchWhenInCandidateList()
    {
        // Sanity: a legitimate AI judgment that picks an actual candidate still works.
        var repo = new FakeServiceRepository();
        repo.Seed("plumbing");
        repo.Seed("carpentry");
        repo.SimilarityRule = (existing, _) => existing == "plumbing" ? 0.65 : 0;
        var ai = new ScriptedAi { JudgeResult = new ServiceJudgeResult("plumbing", false, null) };
        var resolver = Build(repo, ai);

        var result = await resolver.ResolveAsync("pipe-fix", "leaky pipe");

        result.CanonicalSlug.ShouldBe("plumbing");
        result.Resolution.ShouldBe(SlugResolution.AiJudgedMerge);
    }

    private sealed class FakeServiceRepository : IServiceRepository
    {
        private readonly Dictionary<string, Service> _store = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, string, double> SimilarityRule { get; set; } = (_, _) => 0;

        public void Seed(string slug)
        {
            _store[slug] = Service.Create(slug);
        }

        public Service? GetSeed(string slug) => _store.TryGetValue(slug, out var s) ? s : null;

        public Task<Service?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(slug, out var s) ? s : null);

        public Task<IReadOnlyList<SlugSimilarity>> FindSimilarAsync(string slug, int take, CancellationToken ct = default)
        {
            var matches = _store.Keys
                .Select(s => new SlugSimilarity(s, SimilarityRule(s, slug)))
                .Where(x => x.Similarity > 0)
                .OrderByDescending(x => x.Similarity)
                .Take(take)
                .ToList();
            return Task.FromResult<IReadOnlyList<SlugSimilarity>>(matches);
        }

        public Task AddAsync(Service service, CancellationToken ct = default)
        {
            _store[service.Slug] = service;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ScriptedAi : IConversationAi
    {
        public ServiceJudgeResult JudgeResult { get; init; } = new(null, true, null);

        public Task<IntentDetectionResult> DetectIntentAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new IntentDetectionResult(IntentKind.Unknown, 0, "en", null));

        public Task<ServiceExtractionResult> ExtractServicesAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new ServiceExtractionResult(Array.Empty<string>()));

        public Task<ServiceJudgeResult> JudgeServiceMatchAsync(string proposedSlug, IReadOnlyList<string> candidateSlugs, CancellationToken ct = default) =>
            Task.FromResult(JudgeResult);

        public Task<string> GenerateReplyAsync(ReplyContext context, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<LanguageDetectionResult> DetectLanguageAsync(string userMessage, CancellationToken ct = default) =>
            Task.FromResult(new LanguageDetectionResult("en", 1));

        public Task<DateTimeOffset?> ExtractEtaAsync(string userMessage, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<DateTimeOffset?>(null);
    }
}
