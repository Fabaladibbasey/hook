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
    private readonly List<ApplyClientServiceResolutionCommand> _applied = [];
    private readonly List<ResetClientServiceResolutionCommand> _reset = [];

    public ExtractServicesHandlerTests()
    {
        _slugResolverMock = new Mock<SlugResolver>(
            Mock.Of<IServiceRepository>(),
            _aiMock.Object,
            _busMock.Object,
            Options.Create(new ServiceTaxonomyOptions()),
            TimeProvider.System,
            NullLogger<SlugResolver>.Instance,
            null!)
        { CallBase = false };
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ApplyClientServiceResolutionCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _applied.Add((ApplyClientServiceResolutionCommand)m))
            .Returns(ValueTask.CompletedTask);
        _busMock.Setup(x => x.PublishAsync(It.IsAny<ResetClientServiceResolutionCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((m, _) => _reset.Add((ResetClientServiceResolutionCommand)m))
            .Returns(ValueTask.CompletedTask);
    }

    private ExtractServicesHandler Build() => new(_aiMock.Object, _slugResolverMock.Object);

    [Fact]
    public async Task Handle_AiReturnsNoSlugs_InvokesResetCommand()
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new ExtractServicesCommand("+220300001", "asdfasdf", IsSwitch: false),
            _busMock.Object, CancellationToken.None);

        _reset.ShouldHaveSingleItem();
        _reset[0].IsSwitch.ShouldBeFalse();
        _applied.ShouldBeEmpty();
        _slugResolverMock.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AdapterReturnsEmpty_SwitchPath_InvokesResetCommand()
    {
        // OllamaConversationAi.TryCallAsync absorbs transport failures and returns
        // an empty ServiceExtractionResult; handler must short-circuit slug
        // resolution and dispatch a reset on the switch path so the user is acked.
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult([]));

        await Build().Handle(
            new ExtractServicesCommand("+220300001", "I need a plumber", IsSwitch: true),
            _busMock.Object, CancellationToken.None);

        _reset.ShouldHaveSingleItem();
        _reset[0].IsSwitch.ShouldBeTrue();
        _applied.ShouldBeEmpty();
        _slugResolverMock.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AiReturnsSlug_ResolvesAndInvokesApplyCommand(bool isSwitch)
    {
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceExtractionResult(["plumber"]));
        _slugResolverMock
            .Setup(x => x.ResolveAsync("plumber", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveSlugResult("plumbing", SlugResolution.AutoMerged, 0.92));

        await Build().Handle(
            new ExtractServicesCommand("+220300001", "I need a plumber", IsSwitch: isSwitch),
            _busMock.Object, CancellationToken.None);

        _applied.ShouldHaveSingleItem();
        _applied[0].CanonicalSlug.ShouldBe("plumbing");
        _applied[0].IsSwitch.ShouldBe(isSwitch);
        _reset.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_OuterCancellation_RethrowsAndDoesNotInvoke()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _aiMock.Setup(x => x.ExtractServicesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Build().Handle(
            new ExtractServicesCommand("+220300001", "I need a plumber", IsSwitch: false),
            _busMock.Object, cts.Token));

        _applied.ShouldBeEmpty();
        _reset.ShouldBeEmpty();
    }
}
