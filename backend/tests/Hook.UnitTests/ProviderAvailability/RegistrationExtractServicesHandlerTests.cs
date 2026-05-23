using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ProviderAvailability.Register.AdvanceDraft;
using Hook.Features.ProviderAvailability.Register.ExtractServices;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Logging;
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
    private readonly Mock<ILogger<RegistrationExtractServicesHandler>> _loggerMock = new();
    private readonly List<BeginProviderRegistrationCommand> _begin = [];
    private readonly List<ExtendProviderListingCommand> _extend = [];
    private readonly List<AmendRegistrationDraftCommand> _amend = [];
    private readonly List<AmendAddServicesDraftCommand> _amendAdd = [];

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
        // ResolveBatchAsync stub: delegate to the per-slug ResolveAsync mocks
        // sequentially. Tests don't re-implement the production gate / parallel
        // semantics here — that contract is covered by SlugResolverBatchTests
        // (integration) against the real implementation.
        _slugResolverMock.Setup(x => x.ResolveBatchAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<string> slugs, string raw, CancellationToken ct) =>
            {
                var results = new List<ResolveSlugResult>(slugs.Count);
                foreach (var slug in slugs)
                    results.Add(await _slugResolverMock.Object.ResolveAsync(slug, raw, ct));
                return (IReadOnlyList<ResolveSlugResult>)results;
            });
        _busMock.Setup(x => x.PublishAsync(It.IsAny<BeginProviderRegistrationCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _begin.Add((BeginProviderRegistrationCommand)m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ExtendProviderListingCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _extend.Add((ExtendProviderListingCommand)m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<AmendRegistrationDraftCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _amend.Add((AmendRegistrationDraftCommand)m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<AmendAddServicesDraftCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _amendAdd.Add((AmendAddServicesDraftCommand)m))
            .Returns(ValueTask.CompletedTask);
    }

    private RegistrationExtractServicesHandler Build() =>
        new(_aiMock.Object, _slugResolverMock.Object, _loggerMock.Object);

    [Fact]
    public async Task Handle_NoSlugs_InvokesBeginWithEmptyList()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "asdf", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _begin.ShouldHaveSingleItem();
        _begin[0].CanonicalSlugs.ShouldBeEmpty();
        _extend.ShouldBeEmpty();
        _amend.ShouldBeEmpty();
        _amendAdd.ShouldBeEmpty();
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
            new RegistrationExtractServicesCommand("+220300001", "I offer plumbing and carpentry", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None);

        _begin.ShouldHaveSingleItem();
        _begin[0].CanonicalSlugs.ShouldBe(["plumbing", "carpentry"]);
        _extend.ShouldBeEmpty();
        _amend.ShouldBeEmpty();
        _amendAdd.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AddToExistingMode_InvokesExtendCommand()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "I offer plumbing", RegistrationExtractMode.AddToExisting),
            _busMock.Object, CancellationToken.None);

        _extend.ShouldHaveSingleItem();
        _extend[0].CanonicalSlugs.ShouldBeEmpty();
        _begin.ShouldBeEmpty();
        _amend.ShouldBeEmpty();
        _amendAdd.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AppendToDraftMode_InvokesAmendCommand()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "and carpentry", RegistrationExtractMode.AppendToDraft),
            _busMock.Object, CancellationToken.None);

        _amend.ShouldHaveSingleItem();
        _begin.ShouldBeEmpty();
        _extend.ShouldBeEmpty();
        _amendAdd.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AppendToAddDraftMode_InvokesAmendAddCommand()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "and carpentry", RegistrationExtractMode.AppendToAddDraft),
            _busMock.Object, CancellationToken.None);

        _amendAdd.ShouldHaveSingleItem();
        _begin.ShouldBeEmpty();
        _extend.ShouldBeEmpty();
        _amend.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_SlugResolverThrows_Propagates()
    {
        // After the AI-catch consolidation the handler no longer absorbs
        // SlugResolver failures. Errors propagate to Wolverine, which retries on
        // transient pg states (Shared/Messaging.TransientPgStates) and DLQs after
        // MaxAttempts. The exception aborts before bus.InvokeAsync so no
        // half-resolved batch ever reaches the orchestrator. Mock the method
        // production actually calls (ResolveBatchAsync), overriding the
        // delegating ctor setup so the throw isn't muted.
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(["plumber", "carpentry"]));
        _slugResolverMock.Setup(x => x.ResolveBatchAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("slug resolver blew up"));

        await Should.ThrowAsync<InvalidOperationException>(() => Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "plumber and carpentry", RegistrationExtractMode.NewRegistration),
            _busMock.Object, CancellationToken.None));

        _begin.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_OuterCancellation_RethrowsAndDoesNotInvoke()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new RegistrationExtractServicesCommand("+220300001", "I offer plumbing", RegistrationExtractMode.NewRegistration),
            _busMock.Object, cts.Token));

        _begin.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_UnknownMode_LogsWarningAndDoesNotPublish()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        var cmd = new RegistrationExtractServicesCommand(
            "+220300001",
            "I offer plumbing",
            (RegistrationExtractMode)999);

        await Build().Handle(cmd, _busMock.Object, default);

        _begin.ShouldBeEmpty();
        _extend.ShouldBeEmpty();
        _amend.ShouldBeEmpty();
        _amendAdd.ShouldBeEmpty();
        _loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((_, _) => true),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
