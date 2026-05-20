using System.Diagnostics;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ProviderAvailability.Register.ExtractServices;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ProviderAvailability;

public class RegistrationExtractServicesHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<SlugResolver> _slugResolverMock;
    private readonly List<AdvanceRegistrationDraft> _invoked = [];

    public RegistrationExtractServicesHandlerTests()
    {
        _slugResolverMock = new Mock<SlugResolver>(
            Mock.Of<IServiceRepository>(),
            _aiMock.Object,
            _busMock.Object,
            Options.Create(new ServiceTaxonomyOptions()),
            NullLogger<SlugResolver>.Instance,
            null!)
        { CallBase = false };
        _slugResolverMock.Setup(x => x.ResolveBatchAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<string> slugs, string raw, CancellationToken ct) =>
            {
                using var gate = new SemaphoreSlim(4, 4);
                var tasks = slugs.Select(async slug =>
                {
                    await gate.WaitAsync(ct);
                    try { return await _slugResolverMock.Object.ResolveAsync(slug, raw, ct); }
                    finally { gate.Release(); }
                });
                IReadOnlyList<ResolveSlugResult> results = await Task.WhenAll(tasks);
                return results;
            });
        _busMock.Setup(x => x.InvokeAsync(It.IsAny<AdvanceRegistrationDraft>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<object, CancellationToken, TimeSpan?>((m, _, _) => _invoked.Add((AdvanceRegistrationDraft)m))
            .Returns(Task.CompletedTask);
    }

    private RegistrationExtractServicesHandler Build() =>
        new(_aiMock.Object, _slugResolverMock.Object, NullLogger<RegistrationExtractServicesHandler>.Instance);

    [Fact]
    public async Task Handle_NoSlugs_InvokesAdvanceWithEmptyList()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "asdf", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlugs.ShouldBeEmpty();
        _invoked[0].Mode.ShouldBe(RegistrationExtractMode.NewRegistration);
    }

    [Fact]
    public async Task Handle_MultipleSlugs_ResolvesAndPassesAll()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(["plumber", "carpentry"]));
        _slugResolverMock.Setup(x => x.ResolveAsync("plumber", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveSlugResult("plumbing", SlugResolution.AutoMerged, 0.9));
        _slugResolverMock.Setup(x => x.ResolveAsync("carpentry", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveSlugResult("carpentry", SlugResolution.AutoMerged, 0.95));

        await Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "I offer plumbing and carpentry", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlugs.ShouldBe(["plumbing", "carpentry"]);
    }

    [Fact]
    public async Task Handle_AiThrows_InvokesAdvanceWithEmptyList()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        await Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "I offer plumbing", RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlugs.ShouldBeEmpty();
        _invoked[0].Mode.ShouldBe(RegistrationExtractMode.AddToExisting);
    }

    [Fact]
    public async Task Handle_SlugResolverThrows_PoisonsWholeBatch_InvokesEmpty()
    {
        // Catch-all guarantees that one bad slug doesn't half-fill the canonical list and
        // lead to half-published "Updated: …" reply lines. Falls back to empty so the
        // orchestrator goes through the no-slug branch.
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(["plumber", "carpentry"]));
        _slugResolverMock.Setup(x => x.ResolveAsync("plumber", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveSlugResult("plumbing", SlugResolution.AutoMerged, 0.9));
        _slugResolverMock.Setup(x => x.ResolveAsync("carpentry", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("slug resolver blew up"));

        await Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "plumber and carpentry", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlugs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_CancellationRequested_RethrowsAndDoesNotInvoke()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "I offer plumbing", RegistrationExtractMode.NewRegistration),
            _busMock.Object, cts.Token));

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_MultipleSlugs_ResolvesInParallel()
    {
        string[] slugs = ["plumbing", "electrical", "carpentry", "tiling", "painting", "roofing", "welding", "masonry"];
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(slugs));

        var perSlugDelay = TimeSpan.FromMilliseconds(200);
        foreach (var slug in slugs)
        {
            var captured = slug;
            _slugResolverMock.Setup(x => x.ResolveAsync(captured, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string s, string _, CancellationToken ct) =>
                {
                    await Task.Delay(perSlugDelay, ct);
                    return new ResolveSlugResult(s, SlugResolution.AutoMerged, 0.9);
                });
        }

        var sw = Stopwatch.StartNew();
        await Build().Handle(
            new RegistrationExtractServicesRequested("+220300001", "I do everything", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);
        sw.Stop();

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlugs.ShouldBe(slugs);

        // Sequential = 8 * 200ms = 1600ms. Bounded parallel (cap=4) = 2 batches = ~400ms.
        // 900ms gives margin for CI scheduling noise.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(900),
            $"Resolution appears sequential: {sw.ElapsedMilliseconds}ms");
    }
}
