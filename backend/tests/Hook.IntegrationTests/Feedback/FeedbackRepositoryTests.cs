using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook.IntegrationTests.Feedback;

[Collection("Pipeline-2")]
public sealed class FeedbackRepositoryTests : PipelineTestBase
{
    public FeedbackRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task UpsertStatsAsync_Detached_NewRow_Inserts()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var phone = UniquePhone();

        var stats = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
        stats.RecordOutcome(success: true, DateTimeOffset.UtcNow);
        await repo.UpsertStatsAsync(stats);

        var loaded = await db.ProviderStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProviderPhone == phone);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.CompletedCount);
    }

    [Fact]
    public async Task UpsertStatsAsync_Detached_ExistingRow_UpdatesWithoutPkConflict()
    {
        var phone = UniquePhone();

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
            var initial = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
            await repo.UpsertStatsAsync(initial);
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
            var brandNew = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
            brandNew.RecordOutcome(success: true, DateTimeOffset.UtcNow);
            brandNew.RecordOutcome(success: true, DateTimeOffset.UtcNow);
            await repo.UpsertStatsAsync(brandNew);
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var loaded = await db.ProviderStats.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProviderPhone == phone);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.CompletedCount);
            Assert.Equal(1.0, loaded.SuccessRate, precision: 6);
        }
    }

    [Fact]
    public async Task UpsertStatsAsync_StaleConcurrencyToken_Throws()
    {
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using var seedScope = _fx.Factory.Services.CreateAsyncScope();
        var seedRepo = seedScope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        await seedRepo.UpsertStatsAsync(ProviderStats.Initial(phone, now));

        // Two independent scopes load the same row, mutate, save. The second save
        // must fail because the LastUpdated concurrency token shifts on the first.
        await using var scopeA = _fx.Factory.Services.CreateAsyncScope();
        await using var scopeB = _fx.Factory.Services.CreateAsyncScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IFeedbackRepository>();

        var statsA = await repoA.GetStatsAsync(phone);
        var statsB = await repoB.GetStatsAsync(phone);
        Assert.NotNull(statsA);
        Assert.NotNull(statsB);

        statsA!.RecordOutcome(true, now.AddSeconds(1));
        await repoA.UpsertStatsAsync(statsA);

        statsB!.RecordOutcome(false, now.AddSeconds(2));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => repoB.UpsertStatsAsync(statsB));
    }

    [Fact]
    public async Task UpsertStatsAsync_Tracked_ExistingRow_PersistsMutation()
    {
        var phone = UniquePhone();

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var stats = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
        await repo.UpsertStatsAsync(stats);

        // Re-load via the repository so it is tracked, mutate, and upsert again.
        var tracked = await repo.GetStatsAsync(phone);
        Assert.NotNull(tracked);
        tracked!.RecordOutcome(success: false, DateTimeOffset.UtcNow);
        await repo.UpsertStatsAsync(tracked);

        var loaded = await db.ProviderStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProviderPhone == phone);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.CompletedCount);
        Assert.Equal(0.0, loaded.SuccessRate, precision: 6);
    }

    [Fact]
    public async Task GetLatestPendingForClient_NoMatches_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        Assert.Null(await repo.GetLatestPendingForClientAsync(UniquePhone()));
    }

    [Fact]
    public async Task GetLatestPendingForClient_HasMatchesButNoPending_ReturnsNull()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();

        var (request, match) = await SeedMatchAsync(db, clientPhone);
        var fb = new MatchFeedback
        {
            MatchId = match.Id,
            Step = FeedbackStep.DidYouFind,
            Answer = FeedbackAnswer.Yes,
            RepliedAt = DateTimeOffset.UtcNow
        };
        db.MatchFeedback.Add(fb);
        await db.SaveChangesAsync();

        Assert.Null(await repo.GetLatestPendingForClientAsync(clientPhone));
    }

    [Fact]
    public async Task TryAddPendingAsync_DuplicatePending_ReturnsFalse()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var first = await repo.TryAddPendingAsync(
            new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind });
        Assert.True(first);

        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var second = await repo2.TryAddPendingAsync(
            new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind });
        Assert.False(second);
    }

    [Fact]
    public async Task TryAddPendingAsync_AfterAnswered_AllowsNewPending()
    {
        // The partial unique index applies only to Answer = 'Pending'. Once the first
        // row transitions out of Pending (claimed), a second Pending insert must succeed.
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var entry = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        Assert.True(await repo.TryAddPendingAsync(entry));
        Assert.True(await repo.TryClaimPendingAsync(entry.Id, FeedbackAnswer.Yes, DateTimeOffset.UtcNow));

        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var again = await repo2.TryAddPendingAsync(
            new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind });
        Assert.True(again);
    }

    [Fact]
    public async Task TryClaimPendingAsync_SecondCall_ReturnsFalse()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var entry = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        await repo.TryAddPendingAsync(entry);

        Assert.True(await repo.TryClaimPendingAsync(entry.Id, FeedbackAnswer.Yes, DateTimeOffset.UtcNow));
        Assert.False(await repo.TryClaimPendingAsync(entry.Id, FeedbackAnswer.No, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DeletePendingAsync_OnlyDeletesPending_LeavesAnsweredRow()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var pending = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        await repo.TryAddPendingAsync(pending);
        Assert.True(await repo.DeletePendingAsync(pending.Id));

        // Re-create + claim, then DeletePendingAsync should NOT remove the now-answered row.
        var answered = new MatchFeedback { MatchId = match.Id, Step = FeedbackStep.DidYouFind };
        await repo.TryAddPendingAsync(answered);
        await repo.TryClaimPendingAsync(answered.Id, FeedbackAnswer.Yes, DateTimeOffset.UtcNow);
        Assert.False(await repo.DeletePendingAsync(answered.Id));

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        Assert.NotNull(await verifyDb.MatchFeedback.AsNoTracking().FirstOrDefaultAsync(f => f.Id == answered.Id));
    }

    [Fact]
    public async Task GetLatestPendingForClient_MultiplePending_ReturnsLatestPromptedAt()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();

        var (_, olderMatch) = await SeedMatchAsync(db, clientPhone);
        var (_, newerMatch) = await SeedMatchAsync(db, clientPhone);
        var older = new MatchFeedback
        {
            MatchId = olderMatch.Id,
            Step = FeedbackStep.DidYouFind,
            PromptedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2)
        };
        var newer = new MatchFeedback
        {
            MatchId = newerMatch.Id,
            Step = FeedbackStep.DidYouFind,
            PromptedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)
        };
        db.MatchFeedback.AddRange(older, newer);
        await db.SaveChangesAsync();

        var found = await repo.GetLatestPendingForClientAsync(clientPhone);
        Assert.NotNull(found);
        Assert.Equal(newer.Id, found!.Id);
    }

    private static async Task<(ServiceRequest Request, Match Match)> SeedMatchAsync(
        HookDbContext db, string clientPhone)
    {
        var request = ServiceRequest.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            $"req-{Guid.NewGuid()}", 5.0, DateTimeOffset.UtcNow, false);
        var match = new Match
        {
            RequestId = request.Id,
            ProviderPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}",
            ServiceSlug = "plumbing"
        };
        db.ServiceRequests.Add(request);
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        return (request, match);
    }
}
