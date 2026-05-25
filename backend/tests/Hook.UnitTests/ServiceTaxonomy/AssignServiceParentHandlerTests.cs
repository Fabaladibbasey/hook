using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Moq;
using Shouldly;

namespace Hook.UnitTests.ServiceTaxonomy;

public class AssignServiceParentHandlerTests
{
    private static Mock<IServiceRepository> Repo(params Service[] services)
    {
        var repo = new Mock<IServiceRepository>();
        foreach (var svc in services)
            repo.Setup(r => r.GetBySlugAsync(svc.Slug, It.IsAny<CancellationToken>())).ReturnsAsync(svc);
        return repo;
    }

    [Fact]
    public async Task Handle_AssignsParent_OnRootSvc()
    {
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        var doctor = Service.Create("doctor", DateTimeOffset.UtcNow);
        var repo = Repo(svc, doctor);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("cardiology", "doctor"), CancellationToken.None);

        svc.ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public async Task Handle_NoOp_OnNonRootSvc()
    {
        // Handler short-circuits on !IsRoot — defense-in-depth before the
        // aggregate-level re-parent guard (Service.AssignParent throws).
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        svc.AssignParent(Service.Create("doctor", DateTimeOffset.UtcNow));
        var lawyer = Service.Create("lawyer", DateTimeOffset.UtcNow);
        var repo = Repo(svc, lawyer);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("cardiology", "lawyer"), CancellationToken.None);

        svc.ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public async Task Handle_NoOp_WhenSvcMissing()
    {
        var repo = new Mock<IServiceRepository>();
        repo.Setup(r => r.GetBySlugAsync("gone", It.IsAny<CancellationToken>())).ReturnsAsync((Service?)null);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("gone", "doctor"), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_NoOp_WhenParentMissing()
    {
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        var repo = Repo(svc);
        repo.Setup(r => r.GetBySlugAsync("ghost", It.IsAny<CancellationToken>())).ReturnsAsync((Service?)null);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("cardiology", "ghost"), CancellationToken.None);

        svc.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_NoOp_WhenParentNonRoot()
    {
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        var mid = Service.Create("internal-medicine", DateTimeOffset.UtcNow);
        mid.AssignParent(Service.Create("doctor", DateTimeOffset.UtcNow));
        var repo = Repo(svc, mid);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("cardiology", "internal-medicine"), CancellationToken.None);

        svc.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_NoOp_WhenSelfParent()
    {
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        var repo = Repo(svc);

        await new AssignServiceParentHandler(repo.Object)
            .Handle(new AssignServiceParentCommand("cardiology", "cardiology"), CancellationToken.None);

        svc.ParentSlug.ShouldBeNull();
    }
}
