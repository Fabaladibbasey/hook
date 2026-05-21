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
        // Distinct stems + independent guid tails: trigram similarity stays well below
        // AutoMergeThreshold (verified by the explicit similarity probe in
        // TrigramSimilarPeers_BothCreatedAndPublished). This test asserts the happy-path
        // (two genuinely new slugs both create + publish); the intra-batch-peer race is
        // covered by TrigramSimilarPeers_BothCreatedAndPublished.
        var slugs = new[]
        {
            $"newresolve-alpha-{Guid.NewGuid():N}"[..24],
            $"diffshape-omega-{Guid.NewGuid():N}"[..24],
        };

        IReadOnlyList<ResolveSlugResult> resolved = [];
        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                resolved = await resolver.ResolveBatchAsync(slugs, rawExample: "test", default);
            }));

        resolved.Select(r => r.Resolution).ShouldAllBe(r => r == SlugResolution.Created);

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
        var existing = $"preseed-existing-{Guid.NewGuid():N}"[..24];
        var brand1 = $"alphafresh-{Guid.NewGuid():N}"[..18];
        var brand2 = $"omegafresh-{Guid.NewGuid():N}"[..18];

        // Pre-seed the existing service so the resolver short-circuits the GetBySlug path.
        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            db.Services.Add(Service.Create(existing, "pre-seed"));
            await db.SaveChangesAsync();
        }

        var slugs = new[] { existing, brand1, brand2 };
        IReadOnlyList<ResolveSlugResult> resolved = [];
        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                resolved = await resolver.ResolveBatchAsync(slugs, rawExample: "test", default);
            }));

        var resolvedBySlug = resolved.ToDictionary(r => r.CanonicalSlug, StringComparer.Ordinal);
        resolvedBySlug[existing].Resolution.ShouldBe(SlugResolution.ReturnedExisting);
        resolvedBySlug[brand1].Resolution.ShouldBe(SlugResolution.Created);
        resolvedBySlug[brand2].Resolution.ShouldBe(SlugResolution.Created);

        var publishedSlugs = tracked.Sent.MessagesOf<JudgeParentSlugRequested>()
            .Select(e => e.Slug)
            .Where(s => slugs.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        publishedSlugs.ShouldBe(new[] { brand1, brand2 }.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ResolveBatchAsync_TrigramSimilarPeers_BothCreatedAndPublished()
    {
        // pg_trgm extracts character trigrams over the whole padded string (hyphens are
        // literal characters, not separators). pg_trgm similarity is Jaccard on trigram
        // sets — to clear AutoMergeThreshold (0.85) with a single-char diff (which
        // perturbs ~3 trigrams), slugs must be long enough that 3 trigrams are dwarfed by
        // the shared run. We use a 32-char shared guid plus a 4-char tail differing in
        // the last character — ~41 chars total, ~42 trigrams, sim ≈ (42-3)/(42+3) ≈ 0.87.
        // The explicit similarity probe below pins the premise. Without the intra-batch
        // peer guard, the inner context whose SaveChanges runs second would see the first
        // row via FindSimilarAsync (Postgres default isolation: READ COMMITTED) and
        // auto-merge into it — silently dropping the loser. This test locks in that both
        // rows are created and both envelopes are published regardless of commit order.
        var shared = Guid.NewGuid().ToString("N");
        var slugs = new[] { $"sim-{shared}-aaaa", $"sim-{shared}-aaab" };

        // Sanity-check the premise: if pg_trgm doesn't actually score these peers above
        // AutoMergeThreshold, the test is not exercising the filter at all. Catch a pg_trgm
        // tuning change as a clear assertion failure here instead of as a flaky "test passes
        // even with the production guard removed" silent regression.
        await using (var probe = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = probe.ServiceProvider.GetRequiredService<HookDbContext>();
            var sim = await db.Database.SqlQuery<double>(
                $"SELECT similarity({slugs[0]}, {slugs[1]}) AS \"Value\"").SingleAsync();
            sim.ShouldBeGreaterThanOrEqualTo(0.85,
                $"pg_trgm similarity({slugs[0]}, {slugs[1]}) = {sim:F3}; test no longer exercises the auto-merge race");
        }

        IReadOnlyList<ResolveSlugResult> resolved = [];
        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                resolved = await resolver.ResolveBatchAsync(slugs, rawExample: "test", default);
            }));

        resolved.Select(r => r.Resolution).ShouldAllBe(r => r == SlugResolution.Created);

        var publishedSlugs = tracked.Sent.MessagesOf<JudgeParentSlugRequested>()
            .Select(e => e.Slug)
            .Where(s => slugs.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        publishedSlugs.ShouldBe(slugs.OrderBy(s => s, StringComparer.Ordinal).ToArray());

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var db2 = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var rows = await db2.Services
            .Where(s => slugs.Contains(s.Slug))
            .Select(s => s.Slug)
            .ToListAsync();
        rows.OrderBy(s => s, StringComparer.Ordinal).ToArray()
            .ShouldBe(slugs.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ResolveBatchAsync_SingleNormalizedAfterDedup_SequentialPath_PublishesOnce()
    {
        // Two raw inputs that collapse to the same normalized slug must dedupe at the
        // boundary AND fall into the sequential path (Count==1 after dedupe). Verifies
        // the post-commit publish fires exactly once on the outer ambient context.
        var unique = Guid.NewGuid().ToString("N")[..8];
        var raws = new[] { $"DedupRaw-{unique}", $"dedupraw-{unique}-" }; // both → "dedupraw-{unique}"
        var normalized = SlugResolver.Normalize(raws[0]);

        IReadOnlyList<ResolveSlugResult> resolved = [];
        var host = _fx.Factory.Services.GetRequiredService<IHost>();
        var tracked = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = _fx.Factory.Services.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<SlugResolver>();
                resolved = await resolver.ResolveBatchAsync(raws, rawExample: "test", default);
            }));

        resolved.Count.ShouldBe(1);
        resolved[0].CanonicalSlug.ShouldBe(normalized);
        resolved[0].Resolution.ShouldBe(SlugResolution.Created);

        tracked.Sent.MessagesOf<JudgeParentSlugRequested>()
            .Count(e => e.Slug == normalized)
            .ShouldBe(1);
    }
}
