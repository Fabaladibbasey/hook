using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ResolveSlug;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace Hook.IntegrationTests.ServiceTaxonomy;

[Collection("Pipeline-Migration")]
public sealed class SlugResolverBatchTests : PipelineTestBase
{
    public SlugResolverBatchTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task ResolveBatchAsync_PublishesJudgeParentEnvelope_OnOuterContext_PerNewSlug()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var slugs = new[] { $"batchnew-a-{unique}", $"batchnew-b-{unique}" };

        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                await resolver.ResolveBatchAsync(slugs, rawExample: "test", default);
            }));

        var publishedSlugs = tracked.Sent.MessagesOf<JudgeParentSlugRequested>()
            .Select(e => e.Slug)
            .Where(s => slugs.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        publishedSlugs.ShouldBe(slugs.OrderBy(s => s, StringComparer.Ordinal).ToArray());

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var rows = await db.Services
            .Where(s => slugs.Contains(s.Slug))
            .Select(s => s.Slug)
            .ToListAsync();
        rows.OrderBy(s => s, StringComparer.Ordinal).ToArray()
            .ShouldBe(slugs.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ResolveBatchAsync_MixedNewAndExisting_PublishesOnlyForNewSlugs()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var existing = $"batchexisting-{unique}";
        var brand1 = $"batchbrand1-{unique}";
        var brand2 = $"batchbrand2-{unique}";

        // Pre-seed the existing service so the resolver short-circuits the GetBySlug path.
        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            db.Services.Add(Service.Create(existing, "pre-seed"));
            await db.SaveChangesAsync();
        }

        var slugs = new[] { existing, brand1, brand2 };
        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                await resolver.ResolveBatchAsync(slugs, rawExample: "test", default);
            }));

        var publishedSlugs = tracked.Sent.MessagesOf<JudgeParentSlugRequested>()
            .Select(e => e.Slug)
            .Where(s => slugs.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        publishedSlugs.ShouldBe(new[] { brand1, brand2 }.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
}
