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
    private readonly StubDedupGate _dedup = new(claim: true);
    private readonly List<AssignServiceParentCommand> _invoked = [];

    public JudgeParentSlugDispatchHandlerTests()
    {
        _bus.Setup(b => b.PublishAsync(It.IsAny<AssignServiceParentCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions?>((cmd, _) => _invoked.Add((AssignServiceParentCommand)cmd))
            .Returns(ValueTask.CompletedTask);
    }

    private JudgeParentSlugDispatchHandler Build() =>
        new(_repo.Object, _ai.Object, _dedup, _logger.Object);

    private sealed class StubDedupGate(bool claim) : IJudgeParentDedupGate
    {
        public int CallCount { get; private set; }
        public bool Claim { get; set; } = claim;
        public Task<bool> TryClaimAsync(string slug, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Claim);
        }
    }

    private void SetupSlug(Service svc) =>
        _repo.Setup(r => r.GetBySlugAsync(svc.Slug, It.IsAny<CancellationToken>())).ReturnsAsync(svc);

    [Fact]
    public async Task Handle_AssignsParent_WhenAiReturnsRoot()
    {
        SetupSlug(Service.Create("cardiology", DateTimeOffset.UtcNow));
        _ai.Setup(a => a.JudgeParentSlugAsync("cardiology", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doctor");

        await Build().Handle(new JudgeParentSlugCommand("cardiology"), _bus.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].Slug.ShouldBe("cardiology");
        _invoked[0].ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public async Task Handle_NoOp_WhenSlugIsAlreadyARoot()
    {
        // "doctor" is a seeded RootSlug — handler must skip without calling AI.
        SetupSlug(Service.Create("doctor", DateTimeOffset.UtcNow));

        await Build().Handle(new JudgeParentSlugCommand("doctor"), _bus.Object, CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _ai.Verify(a => a.JudgeParentSlugAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoOp_WhenAiReturnsNull()
    {
        SetupSlug(Service.Create("astrology", DateTimeOffset.UtcNow));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Build().Handle(new JudgeParentSlugCommand("astrology"), _bus.Object, CancellationToken.None);

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AiThrows_Propagates()
    {
        // AI failure absorption lives in OllamaConversationAi.TryCallAsync — the
        // production adapter returns null on transport failure, so this path
        // doesn't fire. A mock that throws bubbles past the handler unchanged
        // (no try-catch); Wolverine handles transient retry + DLQ.
        SetupSlug(Service.Create("astrology", DateTimeOffset.UtcNow));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        await Should.ThrowAsync<HttpRequestException>(() =>
            Build().Handle(new JudgeParentSlugCommand("astrology"), _bus.Object, CancellationToken.None));

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_PropagatesOperationCanceled()
    {
        SetupSlug(Service.Create("astrology", DateTimeOffset.UtcNow));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("shutdown"));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Build().Handle(new JudgeParentSlugCommand("astrology"), _bus.Object, CancellationToken.None));

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoOp_WhenAiReturnsOutOfListParent()
    {
        SetupSlug(Service.Create("cardiology", DateTimeOffset.UtcNow));
        _ai.Setup(a => a.JudgeParentSlugAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("evil-slug");

        await Build().Handle(new JudgeParentSlugCommand("cardiology"), _bus.Object, CancellationToken.None);

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
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        svc.AssignParent(Service.Create("doctor", DateTimeOffset.UtcNow));
        SetupSlug(svc);

        await Build().Handle(new JudgeParentSlugCommand("cardiology"), _bus.Object, CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _ai.Verify(a => a.JudgeParentSlugAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcDeleted()
    {
        _repo.Setup(r => r.GetBySlugAsync("gone", It.IsAny<CancellationToken>())).ReturnsAsync((Service?)null);

        await Build().Handle(new JudgeParentSlugCommand("gone"), _bus.Object, CancellationToken.None);

        _invoked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_PublishesAssignCommand_WhenAiReturnsValidParent()
    {
        // Dispatch handler publishes AssignServiceParentCommand to the durable outbox;
        // AssignServiceParentHandler commits the mutation in its own transaction.
        // This test verifies the dispatch handler itself does not mutate the aggregate.
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        _repo.Setup(r => r.GetBySlugAsync("cardiology", It.IsAny<CancellationToken>()))
             .ReturnsAsync(svc);
        _ai.Setup(a => a.JudgeParentSlugAsync("cardiology", It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("doctor");

        await Build().Handle(new JudgeParentSlugCommand("cardiology"), _bus.Object, CancellationToken.None);

        _invoked.ShouldHaveSingleItem();
        _invoked[0].Slug.ShouldBe("cardiology");
        _invoked[0].ParentSlug.ShouldBe("doctor");
        svc.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_NoOp_WhenDedupGateRejectsClaim()
    {
        SetupSlug(Service.Create("cardiology", DateTimeOffset.UtcNow));
        _dedup.Claim = false;

        await Build().Handle(new JudgeParentSlugCommand("cardiology"), _bus.Object, CancellationToken.None);

        _invoked.ShouldBeEmpty();
        _ai.Verify(a => a.JudgeParentSlugAsync(It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
