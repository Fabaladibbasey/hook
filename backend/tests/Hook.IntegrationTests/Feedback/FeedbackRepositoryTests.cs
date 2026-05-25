using Hook.Features.Feedback;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook.IntegrationTests.Feedback;

[Collection("Pipeline-2")]
public sealed class FeedbackRepositoryTests : PipelineTestBase
{
    public FeedbackRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

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
        await db.SaveChangesAsync();

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
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var initial = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
            await repo.UpsertStatsAsync(initial);
            await db.SaveChangesAsync();
        }

        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
            var brandNew = ProviderStats.Initial(phone, DateTimeOffset.UtcNow);
            brandNew.RecordOutcome(success: true, DateTimeOffset.UtcNow);
            brandNew.RecordOutcome(success: true, DateTimeOffset.UtcNow);
            await repo.UpsertStatsAsync(brandNew);
            await db.SaveChangesAsync();
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
        var seedDb = seedScope.ServiceProvider.GetRequiredService<HookDbContext>();
        await seedRepo.UpsertStatsAsync(ProviderStats.Initial(phone, now));
        await seedDb.SaveChangesAsync();

        // Two independent scopes load the same row, mutate, save. The second save
        // must fail because the LastUpdated concurrency token shifts on the first.
        await using var scopeA = _fx.Factory.Services.CreateAsyncScope();
        await using var scopeB = _fx.Factory.Services.CreateAsyncScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var dbA = scopeA.ServiceProvider.GetRequiredService<HookDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<HookDbContext>();

        var statsA = await repoA.GetStatsAsync(phone);
        var statsB = await repoB.GetStatsAsync(phone);
        Assert.NotNull(statsA);
        Assert.NotNull(statsB);

        statsA!.RecordOutcome(true, now.AddSeconds(1));
        await repoA.UpsertStatsAsync(statsA);
        await dbA.SaveChangesAsync();

        statsB!.RecordOutcome(false, now.AddSeconds(2));
        await repoB.UpsertStatsAsync(statsB);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
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
        await db.SaveChangesAsync();

        // Re-load via the repository so it is tracked, mutate, and upsert again.
        var tracked = await repo.GetStatsAsync(phone);
        Assert.NotNull(tracked);
        tracked!.RecordOutcome(success: false, DateTimeOffset.UtcNow);
        await repo.UpsertStatsAsync(tracked);
        await db.SaveChangesAsync();

        var loaded = await db.ProviderStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProviderPhone == phone);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.CompletedCount);
        Assert.Equal(0.0, loaded.SuccessRate, precision: 6);
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
            MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow));
        Assert.True(first);

        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var second = await repo2.TryAddPendingAsync(
            MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow));
        Assert.False(second);
    }

    [Fact]
    public async Task TryAddPendingAsync_AfterStep1Answered_BlocksLateStep1ForSameRequest()
    {
        // ux_match_feedback_request_step1 is filtered on Step=DidYouFind only (not Answer),
        // so once a Step1 row exists for a RequestId — pending or answered — a second
        // Step1 insert must fail. This locks late-arriving Step1FeedbackCheck siblings
        // out after one already won.
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var entry = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        Assert.True(await repo.TryAddPendingAsync(entry));
        entry.ClaimYes(DateTimeOffset.UtcNow);
        Assert.True(await repo.TrySaveAsync(entry));

        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var again = await repo2.TryAddPendingAsync(
            MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow));
        Assert.False(again);
    }

    [Fact]
    public async Task TrySaveAsync_ConcurrentClaim_SecondLoses()
    {
        // Two scopes load the same Pending row (same xmin), both mutate, save A → ok,
        // save B → DbUpdateConcurrencyException is caught and returns false.
        var clientPhone = UniquePhone();
        await using var seedScope = _fx.Factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var seedRepo = seedScope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(seedDb, clientPhone);
        var entry = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        await seedRepo.TryAddPendingAsync(entry);

        await using var scopeA = _fx.Factory.Services.CreateAsyncScope();
        await using var scopeB = _fx.Factory.Services.CreateAsyncScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IFeedbackRepository>();

        var loadedA = await repoA.GetByIdAsync(entry.Id);
        var loadedB = await repoB.GetByIdAsync(entry.Id);
        Assert.NotNull(loadedA);
        Assert.NotNull(loadedB);

        loadedA!.ClaimYes(DateTimeOffset.UtcNow);
        Assert.True(await repoA.TrySaveAsync(loadedA));

        loadedB!.ClaimNo(DateTimeOffset.UtcNow);
        Assert.False(await repoB.TrySaveAsync(loadedB));

        // Pin winner-side persistence: scopeA's ClaimYes survived; the loser
        // did not overwrite with ClaimNo.
        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var persisted = await verifyDb.MatchFeedback
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == entry.Id);
        Assert.NotNull(persisted);
        Assert.Equal(FeedbackAnswer.Yes, persisted!.Answer);
    }

    [Fact]
    public async Task DeletePendingAsync_OnlyDeletesPending_LeavesAnsweredRow()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var pending = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        await repo.TryAddPendingAsync(pending);
        Assert.True(await repo.DeletePendingAsync(pending.Id));

        // Re-create + claim, then DeletePendingAsync should NOT remove the now-answered row.
        var answered = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        await repo.TryAddPendingAsync(answered);
        answered.ClaimYes(DateTimeOffset.UtcNow);
        await repo.TrySaveAsync(answered);
        Assert.False(await repo.DeletePendingAsync(answered.Id));

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        Assert.NotNull(await verifyDb.MatchFeedback.AsNoTracking().FirstOrDefaultAsync(f => f.Id == answered.Id));
    }

    private static async Task<(ServiceRequest Request, Match Match)> SeedMatchAsync(
        HookDbContext db, string clientPhone)
    {
        var request = ServiceRequest.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            $"req-{Guid.NewGuid()}", 5.0, DateTimeOffset.UtcNow, false);
        var match = Match.Create(
            request.Id, $"+220{Random.Shared.Next(0, 10_000_000):D7}", "plumbing", 0, 0, DateTimeOffset.UtcNow);
        db.ServiceRequests.Add(request);
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        return (request, match);
    }

    private static async Task<Match> SeedExtraMatchForRequestAsync(
        HookDbContext db, Guid requestId)
    {
        var match = Match.Create(
            requestId, $"+220{Random.Shared.Next(0, 10_000_000):D7}", "plumbing", 0, 0, DateTimeOffset.UtcNow);
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        return match;
    }

    [Fact]
    public async Task TryAddPending_LateSiblingAfterAnsweredStep1_Rejected()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (request, firstMatch) = await SeedMatchAsync(db, clientPhone);
        var secondMatch = await SeedExtraMatchForRequestAsync(db, request.Id);

        var firstEntry = MatchFeedback.CreatePending(
            firstMatch.Id, firstMatch.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        Assert.True(await repo.TryAddPendingAsync(firstEntry));
        firstEntry.ClaimYes(DateTimeOffset.UtcNow);
        Assert.True(await repo.TrySaveAsync(firstEntry));

        // Late sibling for the same request — broader partial unique includes answered rows.
        var sibling = MatchFeedback.CreatePending(
            secondMatch.Id, secondMatch.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow);
        Assert.False(await repo.TryAddPendingAsync(sibling));
    }

    [Fact]
    public async Task Reschedule_HappyPath_BumpsRecheckCountAndRefreshesPromptedAt()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var entry = MatchFeedback.CreatePending(
            match.Id, match.RequestId, FeedbackStep.DidYouFind,
            DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        await repo.TryAddPendingAsync(entry);

        var bumpAt = DateTimeOffset.UtcNow;
        entry.Reschedule(bumpAt);
        Assert.True(await repo.TrySaveAsync(entry));

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var loaded = await verifyDb.MatchFeedback.AsNoTracking().FirstAsync(f => f.Id == entry.Id);
        Assert.Equal(1, loaded.Step1RecheckCount);
        Assert.True(Math.Abs((loaded.PromptedAt - bumpAt).TotalSeconds) < 1);
    }

    [Fact]
    public async Task CaptureNoReason_HappyPath_SetsAnswerAndReason()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IFeedbackRepository>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        var entry = MatchFeedback.CreatePending(
            match.Id, match.RequestId, FeedbackStep.CaptureNoReason, DateTimeOffset.UtcNow);
        await repo.TryAddPendingAsync(entry);

        entry.CaptureNoReason("too expensive", DateTimeOffset.UtcNow);
        Assert.True(await repo.TrySaveAsync(entry));

        await using var verifyScope = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HookDbContext>();
        var loaded = await verifyDb.MatchFeedback.AsNoTracking().FirstAsync(f => f.Id == entry.Id);
        Assert.Equal(FeedbackAnswer.NoReasonCaptured, loaded.Answer);
        Assert.Equal("too expensive", loaded.NoReason);
    }

    [Fact]
    public async Task TryAddPending_LosingRace_DoesNotPoisonOuterTransaction()
    {
        var clientPhone = UniquePhone();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var (_, match) = await SeedMatchAsync(db, clientPhone);

        await using var tx = await db.Database.BeginTransactionAsync();

        Assert.True(await db.TryInsertUniqueAsync(
            MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow),
            [FeedbackConstants.PendingUniqueIndexName, FeedbackConstants.RequestStep1UniqueIndexName]));

        Assert.False(await db.TryInsertUniqueAsync(
            MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, DateTimeOffset.UtcNow),
            [FeedbackConstants.PendingUniqueIndexName, FeedbackConstants.RequestStep1UniqueIndexName]));

        // Outer tx must still be usable — a poisoned tx would throw here.
        var count = await db.MatchFeedback.CountAsync();
        Assert.True(count >= 1);

        await tx.CommitAsync();
    }
}
