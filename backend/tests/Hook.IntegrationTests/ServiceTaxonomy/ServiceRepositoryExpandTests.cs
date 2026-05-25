using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.ServiceTaxonomy;

[Collection("Pipeline-Migration")]
public sealed class ServiceRepositoryExpandTests : PipelineTestBase
{
    public ServiceRepositoryExpandTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task ExpandAsync_UnknownSlug_ReturnsRequestedOnly()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IServiceRepository>();

        var expanded = await repo.ExpandAsync($"ghost-{Guid.NewGuid():N}");

        expanded.Parent.ShouldBeNull();
        expanded.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpandAsync_RootWithChildren_ReturnsChildrenAndNullParent()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IServiceRepository>();

        var rootSlug = $"root-{Guid.NewGuid():N}";
        var childASlug = $"child-a-{Guid.NewGuid():N}";
        var childBSlug = $"child-b-{Guid.NewGuid():N}";
        var root = Service.Create(rootSlug, DateTimeOffset.UtcNow);
        var childA = Service.Create(childASlug, DateTimeOffset.UtcNow);
        var childB = Service.Create(childBSlug, DateTimeOffset.UtcNow);
        db.Services.AddRange(root, childA, childB);
        await db.SaveChangesAsync();
        childA.AssignParent(root);
        childB.AssignParent(root);
        await db.SaveChangesAsync();

        var expanded = await repo.ExpandAsync(rootSlug);

        expanded.Requested.ShouldBe(rootSlug);
        expanded.Parent.ShouldBeNull();
        expanded.Children.ShouldContain(childASlug);
        expanded.Children.ShouldContain(childBSlug);
    }

    [Fact]
    public async Task ExpandAsync_LeafWithParent_ReturnsParentAndNoChildren()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IServiceRepository>();

        var rootSlug = $"root-{Guid.NewGuid():N}";
        var childSlug = $"leaf-{Guid.NewGuid():N}";
        var root = Service.Create(rootSlug, DateTimeOffset.UtcNow);
        var leaf = Service.Create(childSlug, DateTimeOffset.UtcNow);
        db.Services.AddRange(root, leaf);
        await db.SaveChangesAsync();
        leaf.AssignParent(root);
        await db.SaveChangesAsync();

        var expanded = await repo.ExpandAsync(childSlug);

        expanded.Requested.ShouldBe(childSlug);
        expanded.Parent.ShouldBe(rootSlug);
        expanded.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpandAsync_SingleLevelOnly_DoesNotRecurseGrandchildren()
    {
        // Schema enforces only one level of parenting (Service.AssignParent
        // refuses non-root parents) — ExpandAsync should never see grandchildren
        // for a root. Sanity-check that the SQL Where clause is `Slug = $1 OR
        // ParentSlug = $1` and not a recursive CTE.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IServiceRepository>();

        var rootSlug = $"root-{Guid.NewGuid():N}";
        var childSlug = $"child-{Guid.NewGuid():N}";
        var otherRootSlug = $"other-{Guid.NewGuid():N}";
        var otherChildSlug = $"other-child-{Guid.NewGuid():N}";
        var root = Service.Create(rootSlug, DateTimeOffset.UtcNow);
        var child = Service.Create(childSlug, DateTimeOffset.UtcNow);
        var otherRoot = Service.Create(otherRootSlug, DateTimeOffset.UtcNow);
        var otherChild = Service.Create(otherChildSlug, DateTimeOffset.UtcNow);
        db.Services.AddRange(root, child, otherRoot, otherChild);
        await db.SaveChangesAsync();
        child.AssignParent(root);
        otherChild.AssignParent(otherRoot);
        await db.SaveChangesAsync();

        var expanded = await repo.ExpandAsync(rootSlug);

        expanded.Children.ShouldContain(childSlug);
        expanded.Children.ShouldNotContain(otherChildSlug);
        expanded.Children.ShouldNotContain(otherRootSlug);
    }
}
