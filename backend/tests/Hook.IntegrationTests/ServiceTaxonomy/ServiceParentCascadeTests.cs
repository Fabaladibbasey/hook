using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.ServiceTaxonomy;

[Collection("Pipeline-Migration")]
public sealed class ServiceParentCascadeTests : PipelineTestBase
{
    public ServiceParentCascadeTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task DeletingParent_SetsChildParentSlugToNull_ViaOnDeleteSetNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var parentSlug = $"parent-{Guid.NewGuid():N}";
        var childSlug = $"child-{Guid.NewGuid():N}";

        var parent = Service.Create(parentSlug, DateTimeOffset.UtcNow);
        var child = Service.Create(childSlug, DateTimeOffset.UtcNow);
        db.Services.AddRange(parent, child);
        await db.SaveChangesAsync();

        child.AssignParent(parent);
        await db.SaveChangesAsync();

        db.Services.Remove(parent);
        await db.SaveChangesAsync();

        // Force re-read from DB (state may be cached in the change tracker).
        db.ChangeTracker.Clear();
        var reloaded = await db.Services.SingleAsync(s => s.Slug == childSlug);
        reloaded.ParentSlug.ShouldBeNull();
    }
}
