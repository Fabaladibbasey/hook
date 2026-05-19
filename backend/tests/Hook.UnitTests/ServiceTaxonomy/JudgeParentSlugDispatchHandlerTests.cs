using Hook.Features.Ai;
using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Wolverine;

namespace Hook.UnitTests.ServiceTaxonomy;

public class JudgeParentSlugDispatchHandlerTests
{
    private readonly Mock<IServiceRepository> _repo = new();
    private readonly Mock<IConversationAi> _ai = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly Mock<ILogger<JudgeParentSlugDispatchHandler>> _logger = new();
    private readonly List<AssignServiceParent> _invoked = [];

    public JudgeParentSlugDispatchHandlerTests()
    {
        _bus.Setup(b => b.InvokeAsync(It.IsAny<AssignServiceParent>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<object, CancellationToken, TimeSpan?>((cmd, _, _) => _invoked.Add((AssignServiceParent)cmd))
            .Returns(Task.CompletedTask);
    }

    private JudgeParentSlugDispatchHandler Build() =>
        new(_repo.Object, _ai.Object, _bus.Object, _logger.Object);

    private void SetupSlug(Service svc) =>
        _repo.Setup(r => r.GetBySlugAsync(svc.Slug, It.IsAny<CancellationToken>())).ReturnsAsync(svc);

    [Fact]
    public async Task Handle_AssignsParent_WhenAiReturnsRoot()
    {
        SetupSlug(Service.Create("cardiology"));
        _ai.Setup(a => a.JudgeParentSlugAsync("cardiology", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doctor");

        await Build().Handle(new JudgeParentSlugRequested("cardiology"), CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].Slug.ShouldBe("cardiology");
        _invoked[0].ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public async Task Handle_NoOp_WhenSlugIsAlreadyARoot()
    {
        // "doctor" is a seeded RootSlug — handler must skip without calling AI.
        SetupSlug(Service.Create("doctor"));

        await Build().Handle(new JudgeParentSlugRequested("doctor"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _ai.Verify(a => a.JudgeParentSlugAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoOp_WhenAiReturnsNull()
    {
        SetupSlug(Service.Create("astrology"));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Build().Handle(new JudgeParentSlugRequested("astrology"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoOp_WhenAiThrows()
    {
        SetupSlug(Service.Create("astrology"));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        await Build().Handle(new JudgeParentSlugRequested("astrology"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((_, _) => true),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_PropagatesOperationCanceled()
    {
        SetupSlug(Service.Create("astrology"));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("shutdown"));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Build().Handle(new JudgeParentSlugRequested("astrology"), CancellationToken.None));

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoOp_WhenAiReturnsOutOfListParent()
    {
        SetupSlug(Service.Create("cardiology"));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("evil-slug");

        await Build().Handle(new JudgeParentSlugRequested("cardiology"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((_, _) => true),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcAlreadyHasParent()
    {
        var svc = Service.Create("cardiology");
        svc.AssignParent(Service.Create("doctor"));
        SetupSlug(svc);

        await Build().Handle(new JudgeParentSlugRequested("cardiology"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _ai.Verify(a => a.JudgeParentSlugAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcDeleted()
    {
        _repo.Setup(r => r.GetBySlugAsync("gone", It.IsAny<CancellationToken>())).ReturnsAsync((Service?)null);

        await Build().Handle(new JudgeParentSlugRequested("gone"), CancellationToken.None);

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcDeletedBetweenAiAndAssign()
    {
        // Race: dispatch handler reads svc (call #1), AI infers parent, then
        // retention sweep / manual delete removes the row before the inner
        // AssignServiceParentHandler re-reads it (call #2 returns null).
        var first = Service.Create("cardiology");
        var calls = 0;
        _repo.Setup(r => r.GetBySlugAsync("cardiology", It.IsAny<CancellationToken>()))
             .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1 ? first : null);
        _repo.Setup(r => r.GetBySlugAsync("doctor", It.IsAny<CancellationToken>()))
             .ReturnsAsync(Service.Create("doctor"));
        _ai.Setup(a => a.JudgeParentSlugAsync("cardiology", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doctor");
        _bus.Setup(b => b.InvokeAsync(It.IsAny<AssignServiceParent>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Returns(async (object cmd, CancellationToken ct, TimeSpan? _) =>
                await new AssignServiceParentHandler(_repo.Object).Handle((AssignServiceParent)cmd, ct));

        await Build().Handle(new JudgeParentSlugRequested("cardiology"), CancellationToken.None);

        first.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public void RootSectorSeeder_StaticList_Contains_KnownSeededRoot()
    {
        // Sanity: the handler relies on static RootSlugs membership — this test
        // guards against a future rename of "doctor" silently changing handler
        // behaviour for the canonical specialization examples.
        RootSectorSeeder.RootSlugs.ShouldContain("doctor");
    }
}
