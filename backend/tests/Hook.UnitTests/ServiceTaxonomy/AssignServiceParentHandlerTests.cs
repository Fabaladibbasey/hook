using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ServiceTaxonomy;

public class AssignServiceParentHandlerTests
{
    [Fact]
    public async Task Handle_AssignsParent_OnRootSvc()
    {
        var svc = Service.Create("cardiology");
        var repo = new Mock<IServiceRepository>();
        repo.Setup(r => r.GetBySlugAsync("cardiology", It.IsAny<CancellationToken>())).ReturnsAsync(svc);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParent("cardiology", "doctor"), CancellationToken.None);

        svc.ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public async Task Handle_NoOp_OnNonRootSvc()
    {
        // Handler short-circuits on !IsRoot — defense-in-depth before the
        // aggregate-level re-parent guard (Service.AssignParent throws).
        var svc = Service.Create("cardiology");
        svc.AssignParent("doctor");
        var repo = new Mock<IServiceRepository>();
        repo.Setup(r => r.GetBySlugAsync("cardiology", It.IsAny<CancellationToken>())).ReturnsAsync(svc);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParent("cardiology", "different"), CancellationToken.None);

        svc.ParentSlug.ShouldBe("doctor"); // unchanged, no throw
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcMissing()
    {
        var repo = new Mock<IServiceRepository>();
        repo.Setup(r => r.GetBySlugAsync("gone", It.IsAny<CancellationToken>())).ReturnsAsync((Service?)null);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParent("gone", "doctor"), CancellationToken.None);

        // No exception, no mutation.
    }
}
