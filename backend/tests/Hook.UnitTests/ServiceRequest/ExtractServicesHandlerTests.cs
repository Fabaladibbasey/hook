using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ServiceRequest.Create.AdvanceDraft;
using Hook.Features.ServiceRequest.Create.ExtractServices;
using Hook.Features.ServiceTaxonomy;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceRequest;

public class ExtractServicesHandlerTests
{
    private readonly Mock<IConversationAi> _aiMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<SlugResolver> _slugResolverMock;
    private readonly List<AdvanceClientRequestDraft> _invoked = [];

    public ExtractServicesHandlerTests()
    {
        _slugResolverMock = new Mock<SlugResolver>(
            Mock.Of<IServiceRepository>(),
            _aiMock.Object,
            _busMock.Object,
            Options.Create(new ServiceTaxonomyOptions()),
            NullLogger<SlugResolver>.Instance,
            null!)
        { CallBase = false };
        _busMock.Setup(x => x.InvokeAsync(It.IsAny<AdvanceClientRequestDraft>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<object, CancellationToken, TimeSpan?>((m, _, _) => _invoked.Add((AdvanceClientRequestDraft)m))
            .Returns(Task.CompletedTask);
    }

    private ExtractServicesHandler Build() => new(_aiMock.Object, _slugResolverMock.Object);

    [Fact]
    public async Task Handle_AiReturnsNoSlugs_InvokesAdvanceWithEmptyCanonical()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new ExtractServicesRequested("+220300001", "asdfasdf", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlug.ShouldBe(string.Empty);
        _invoked[0].IsSwitch.ShouldBeFalse();
        _slugResolverMock.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AdapterReturnsEmpty_InvokesAdvanceWithEmptyCanonical()
    {
        // OllamaConversationAi.TryCallAsync absorbs transport failures and returns
        // an empty ServiceExtractionResult; handler must short-circuit slug
        // resolution and advance with an empty canonical slug.
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new ExtractServicesRequested("+220300001", "I need a plumber", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlug.ShouldBe(string.Empty);
        _invoked[0].IsSwitch.ShouldBeTrue();
        _slugResolverMock.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AiReturnsSlug_ResolvesAndInvokesAdvance()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(["plumber"]));
        _slugResolverMock
            .Setup(x => x.ResolveAsync("plumber", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveSlugResult("plumbing", SlugResolution.AutoMerged, 0.92));

        await Build().Handle(
            new ExtractServicesRequested("+220300001", "I need a plumber", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].CanonicalSlug.ShouldBe("plumbing");
    }

    [Fact]
    public async Task Handle_OuterCancellation_RethrowsAndDoesNotInvoke()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new ExtractServicesRequested("+220300001", "I need a plumber", IsSwitch: false),
            _busMock.Object, cts.Token));

        _invoked.ShouldBeEmpty();
    }
}
