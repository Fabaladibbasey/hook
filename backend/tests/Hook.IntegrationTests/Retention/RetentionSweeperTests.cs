using System.Diagnostics.Metrics;
using Hook.Features.Ai.PlatformQa;
using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.MetaTemplates;
using Hook.Features.Observability;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Hook.Shared.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hook.IntegrationTests.Retention;

/// <summary>
/// Exercises <see cref="RetentionSweeper"/> against the shared <see cref="DevPipelineFixture"/>
/// container. The hosted service is disabled in the fixture (Retention:Enabled=false) so the
/// sweeper only runs when these tests invoke it explicitly. Tests use unique phone numbers
/// derived from <see cref="Guid.NewGuid"/> so concurrent test runs do not collide.
/// </summary>
[Collection("Pipeline-4")]
public sealed class RetentionSweeperTests : PipelineTestBase
{
    public RetentionSweeperTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    private static (DateTimeOffset Old, DateTimeOffset Fresh) Boundaries(RetentionOptions opts)
    {
        var now = DateTimeOffset.UtcNow;
        // One full day past the retention cutoff and one hour inside the keep window.
        return (now - TimeSpan.FromDays(opts.RetentionDays + 1), now - TimeSpan.FromHours(1));
    }

    private static IRetentionSweeper Resolve(IServiceProvider sp)
    {
        // Resolve the sweeper but force-enable retention regardless of the test fixture's
        // disabled default. Mirrors what the hosted service would have done in production.
        var db = sp.GetRequiredService<HookDbContext>();
        var configured = sp.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var enabled = Options.Create(new RetentionOptions
        {
            Enabled = true,
            RetentionDays = configured.RetentionDays,
            DeadLetterRetentionDays = configured.DeadLetterRetentionDays,
            PendingFeedbackClaimAfter = configured.PendingFeedbackClaimAfter,
            PlatformAnswerDedupCleanupAfter = configured.PlatformAnswerDedupCleanupAfter,
            SweepInterval = configured.SweepInterval,
            StartupDelay = configured.StartupDelay
        });
        return new RetentionSweeper(
            db, enabled,
            TimeProvider.System,
            NullLogger<RetentionSweeper>.Instance);
    }

    [Fact]
    public async Task Sweep_ResultKeys_MatchEfMappedTableNames()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var sweeper = Resolve(scope.ServiceProvider);

        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        // Wolverine's dead-letter table lives in a separate schema and is not part of the
        // EF model — it is swept via raw SQL. The pending-claimed key is an operation
        // (bulk UPDATE Pending → Skipped) sharing the match_feedback table, not a distinct
        // entity, so it is exempt from the EF-mapped check too.
        var rawSweeps = new HashSet<string>(StringComparer.Ordinal)
        {
            RetentionTableKeys.WolverineDeadLetters,
            RetentionTableKeys.MatchFeedbackPendingClaimed,
        };

        foreach (var key in result.CountsByTable.Keys)
        {
            if (rawSweeps.Contains(key)) continue;
            var found = db.Model.GetEntityTypes()
                .Any(t => string.Equals(t.GetTableName(), key, StringComparison.Ordinal));
            Assert.True(found, $"Sweep key '{key}' has no matching EF entity table");
        }
    }

    [Fact]
    public async Task Sweep_DeletesRowsOlderThanCutoff_KeepsYoungerRows()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, fresh) = Boundaries(opts);

        var oldChat = ChatSession.Create(TimeSpan.FromDays(1), DateTimeOffset.UtcNow - TimeSpan.FromDays(opts.RetentionDays + 2));
        var newChat = ChatSession.Create(TimeSpan.FromDays(1), fresh);
        db.ChatSessions.AddRange(oldChat, newChat);

        var oldRequest = ServiceRequest.Create(
            UniquePhone(), "plumbing",
            new Location(13.45, -16.6),
            "Banjul", "old-test", 5.0, old, false);
        oldRequest.Close();
        var newRequest = ServiceRequest.Create(
            UniquePhone(), "plumbing",
            new Location(13.45, -16.6),
            "Banjul", "fresh-test", 5.0, fresh, false);
        db.ServiceRequests.AddRange(oldRequest, newRequest);

        var oldGeoKey = $"old-{Guid.NewGuid()}";
        var freshGeoKey = $"new-{Guid.NewGuid()}";
        var banjul = new GeocodeResult(new Location(13.45, -16.6), "Banjul", "test", FromCache: false);
        var oldGeo = GeocodeCacheEntry.Capture(oldGeoKey, banjul, old);
        var newGeo = GeocodeCacheEntry.Capture(freshGeoKey, banjul, fresh);
        db.GeocodeCache.AddRange(oldGeo, newGeo);

        var oldContact = WhatsappContact.Recorded(UniquePhone(), old);
        var freshContact = WhatsappContact.Recorded(UniquePhone(), fresh);
        db.WhatsappContacts.AddRange(oldContact, freshContact);

        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.ChatSessions]);
        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.ServiceRequests]);
        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.GeocodeCache]);
        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.WhatsappContacts]);
        Assert.True(result.CountsByTable.ContainsKey(RetentionTableKeys.MatchFeedback));
        Assert.False(result.CountsByTable.ContainsKey("provider_stats"));

        // Old rows gone; fresh control rows survived.
        Assert.False(await db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == oldChat.Id));
        Assert.True(await db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == newChat.Id));
        Assert.False(await db.ServiceRequests.AsNoTracking().AnyAsync(r => r.Id == oldRequest.Id));
        Assert.True(await db.ServiceRequests.AsNoTracking().AnyAsync(r => r.Id == newRequest.Id));
        Assert.False(await db.GeocodeCache.AsNoTracking().AnyAsync(g => g.Key == oldGeoKey));
        Assert.True(await db.GeocodeCache.AsNoTracking().AnyAsync(g => g.Key == freshGeoKey));
        Assert.False(await db.WhatsappContacts.AsNoTracking().AnyAsync(c => c.Phone == oldContact.Phone));
        Assert.True(await db.WhatsappContacts.AsNoTracking().AnyAsync(c => c.Phone == freshContact.Phone));
    }

    [Fact]
    public async Task Sweep_OldServiceRequestStillOpen_NotDeleted()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, _) = Boundaries(opts);

        var openOld = ServiceRequest.Create(
            UniquePhone(), "plumbing",
            new Location(13.45, -16.6),
            "Banjul", "still-open", 5.0, old, false);
        // Status defaults to Open and is *not* closed: sweeper must keep this row.
        db.ServiceRequests.Add(openOld);
        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.True(await db.ServiceRequests.AsNoTracking().AnyAsync(r => r.Id == openOld.Id));

        // Cleanup so this row does not pollute later tests in the shared fixture.
        try
        {
            db.ServiceRequests.Remove(openOld);
            await db.SaveChangesAsync();
        }
        catch
        {
            // Ignore cleanup failures - the row will be reaped on test container teardown.
        }
    }

    [Fact]
    public async Task Sweep_DeletesOldMatchFeedback_KeepsRecent_KeepsProviderStats()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, fresh) = Boundaries(opts);
        var ancient = DateTimeOffset.UtcNow - TimeSpan.FromDays(opts.RetentionDays + 23);

        var (_, oldMatch) = await SeedRequestAndMatchAsync(db, fresh);
        var (_, oldMatch2) = await SeedRequestAndMatchAsync(db, fresh);
        var (_, freshMatch) = await SeedRequestAndMatchAsync(db, fresh);

        var oldAnswered = MatchFeedback.CreatePending(
            oldMatch.Id, oldMatch.RequestId, FeedbackStep.DidYouFind, ancient);
        oldAnswered.ClaimYes(ancient + TimeSpan.FromHours(2));
        var oldPending = MatchFeedback.CreatePending(
            oldMatch2.Id, oldMatch2.RequestId, FeedbackStep.DidYouFind, old);
        var recent = MatchFeedback.CreatePending(
            freshMatch.Id, freshMatch.RequestId, FeedbackStep.JobCompleted, fresh);
        recent.ClaimYes(fresh);
        var phone = UniquePhone();
        var stats = ProviderStats.Initial(phone, ancient);

        db.MatchFeedback.AddRange(oldAnswered, oldPending, recent);
        db.ProviderStats.Add(stats);
        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.CountsByTable[RetentionTableKeys.MatchFeedback]);
        Assert.False(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == oldAnswered.Id));
        Assert.False(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == oldPending.Id));
        Assert.True(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == recent.Id));
        Assert.True(await db.ProviderStats.AsNoTracking().AnyAsync(s => s.ProviderPhone == phone));
    }

    [Fact]
    public async Task Sweep_PendingFeedback_PastWindow_IsClaimedSkipped()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var now = DateTimeOffset.UtcNow;
        // One hour past the claim-after window, one minute inside it.
        var stale = now - opts.PendingFeedbackClaimAfter - TimeSpan.FromHours(1);
        var fresh = now - opts.PendingFeedbackClaimAfter + TimeSpan.FromMinutes(1);

        var (_, staleMatch) = await SeedRequestAndMatchAsync(db, stale);
        var (_, freshMatch) = await SeedRequestAndMatchAsync(db, fresh);

        var stalePending = MatchFeedback.CreatePending(
            staleMatch.Id, staleMatch.RequestId, FeedbackStep.DidYouFind, stale);
        var freshPending = MatchFeedback.CreatePending(
            freshMatch.Id, freshMatch.RequestId, FeedbackStep.DidYouFind, fresh);
        db.MatchFeedback.AddRange(stalePending, freshPending);
        await db.SaveChangesAsync();

        var staleVersion = stalePending.Version;

        var sweeper = Resolve(scope.ServiceProvider);
        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.MatchFeedbackPendingClaimed]);

        var staleAfter = await db.MatchFeedback.AsNoTracking().SingleAsync(f => f.Id == stalePending.Id);
        Assert.Equal(FeedbackAnswer.Skipped, staleAfter.Answer);
        Assert.NotNull(staleAfter.RepliedAt);
        Assert.Equal(staleVersion + 1, staleAfter.Version);

        var freshAfter = await db.MatchFeedback.AsNoTracking().SingleAsync(f => f.Id == freshPending.Id);
        Assert.Equal(FeedbackAnswer.Pending, freshAfter.Answer);
        Assert.Null(freshAfter.RepliedAt);
    }

    [Fact]
    public async Task Sweep_PendingFeedback_TightBoundary_OnlyBarelyStaleIsClaimed()
    {
        // SQL: f.PromptedAt < pendingCutoff (strict less-than).
        // A row 1 second past the boundary IS claimed; 1 second inside is NOT.
        // Tighter margin than the 1h/1min test — documents the strict < semantics.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var now = DateTimeOffset.UtcNow;
        var barelyStale = now - opts.PendingFeedbackClaimAfter - TimeSpan.FromSeconds(1);
        var barelyFresh = now - opts.PendingFeedbackClaimAfter + TimeSpan.FromSeconds(1);

        var (_, staleMatch) = await SeedRequestAndMatchAsync(db, barelyStale);
        var (_, freshMatch) = await SeedRequestAndMatchAsync(db, barelyFresh);

        db.MatchFeedback.AddRange(
            MatchFeedback.CreatePending(staleMatch.Id, staleMatch.RequestId, FeedbackStep.DidYouFind, barelyStale),
            MatchFeedback.CreatePending(freshMatch.Id, freshMatch.RequestId, FeedbackStep.DidYouFind, barelyFresh));
        await db.SaveChangesAsync();

        var result = await Resolve(scope.ServiceProvider).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.CountsByTable[RetentionTableKeys.MatchFeedbackPendingClaimed]);
    }

    [Fact]
    public async Task Sweep_DeletingServiceRequest_CascadesMatchAndFeedback()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, fresh) = Boundaries(opts);

        var oldRequest = ServiceRequest.Create(
            UniquePhone(), "plumbing",
            new Location(13.45, -16.6),
            "Banjul", "cascade-test", 5.0, old, false);
        oldRequest.Close();
        var match = Match.Create(oldRequest.Id, UniquePhone(), "plumbing", 0, 0, old);
        var feedback = MatchFeedback.CreatePending(
            match.Id, match.RequestId, FeedbackStep.DidYouFind, fresh);
        feedback.ClaimYes(fresh);
        db.ServiceRequests.Add(oldRequest);
        db.Matches.Add(match);
        db.MatchFeedback.Add(feedback);
        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.False(await db.ServiceRequests.AsNoTracking().AnyAsync(r => r.Id == oldRequest.Id));
        Assert.False(await db.Matches.AsNoTracking().AnyAsync(m => m.Id == match.Id));
        Assert.False(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == feedback.Id));
    }

    private static async Task<(ServiceRequest Request, Match Match)> SeedRequestAndMatchAsync(
        HookDbContext db, DateTimeOffset createdAt)
    {
        var request = ServiceRequest.Create(
            UniquePhone(), "plumbing",
            new Location(13.45, -16.6),
            "Banjul", $"req-{Guid.NewGuid()}", 5.0, createdAt, false);
        var match = Match.Create(request.Id, UniquePhone(), "plumbing", 0, 0, createdAt);
        db.ServiceRequests.Add(request);
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        return (request, match);
    }

    [Fact]
    public async Task Sweep_ChatSessionDelete_CascadesToMessagesParticipantsAndAccessLogs()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;

        var session = ChatSession.Create(
            TimeSpan.FromDays(1),
            DateTimeOffset.UtcNow - TimeSpan.FromDays(opts.RetentionDays + 2));
        var participant = ChatParticipant.Create(session.Id, ChatParticipantRole.Client, UniquePhone());
        // Seed the post-multi-device columns so cascade has to clear them too.
        participant.SetPublicKey([0x01, 0x02]);
        participant.TryAdvanceSequence(5);
        var message = ChatMessage.Create(
            Guid.CreateVersion7(), session.Id, participant.Id, 1, [9], new byte[12], DateTimeOffset.UtcNow);
        var accessLog = ChatAccessLog.Record(
            session.Id, participant.Id, "127.0.0.1", "test", DateTimeOffset.UtcNow);

        db.ChatSessions.Add(session);
        db.ChatParticipants.Add(participant);
        db.ChatMessages.Add(message);
        db.ChatAccessLogs.Add(accessLog);
        await db.SaveChangesAsync();

        try
        {
            var sweeper = Resolve(scope.ServiceProvider);
            await sweeper.RunOnceAsync(CancellationToken.None);

            Assert.False(await db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == session.Id));
            Assert.False(await db.ChatParticipants.AsNoTracking().AnyAsync(p => p.Id == participant.Id));
            Assert.False(await db.ChatMessages.AsNoTracking().AnyAsync(m => m.Id == message.Id));
            Assert.False(await db.ChatAccessLogs.AsNoTracking().AnyAsync(l => l.Id == accessLog.Id));
        }
        finally
        {
            // If the cascade sweeper failed mid-test, force-delete the seeded session so
            // the shared fixture container does not leak rows into later tests.
            await db.ChatSessions.Where(s => s.Id == session.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Sweep_DeletesExpiredWolverineDeadLetter_KeepsRecent()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var ancient = DateTimeOffset.UtcNow - TimeSpan.FromDays(opts.DeadLetterRetentionDays + 2);
        var fresh = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);

        var oldId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        // sent_at is the timestamptz axis the sweeper compares against.
        // received_at is a varchar listener-URI column and a NOT NULL component of the
        // composite PK (id, received_at) — a constant stub keeps each row unique via its
        // fresh id while still satisfying the NOT NULL.
        const string Insert =
            "INSERT INTO wolverine.wolverine_dead_letters " +
            "(id, message_type, body, received_at, sent_at) " +
            "VALUES ({0}, 'TestMsg', E'\\\\x00', 'local://retention-test', {1})";
        await db.Database.ExecuteSqlRawAsync(Insert, oldId, ancient);
        await db.Database.ExecuteSqlRawAsync(Insert, freshId, fresh);

        try
        {
            var sweeper = Resolve(scope.ServiceProvider);
            var result = await sweeper.RunOnceAsync(CancellationToken.None);

            Assert.True(result.CountsByTable[RetentionTableKeys.WolverineDeadLetters] >= 1,
                "Expected ancient DLQ row to be deleted (must not be -1, which signals SQL error).");

            var oldStill = await db.Database
                .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM wolverine.wolverine_dead_letters WHERE id = {0}", oldId)
                .AnyAsync();
            var freshStill = await db.Database
                .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM wolverine.wolverine_dead_letters WHERE id = {0}", freshId)
                .AnyAsync();
            Assert.False(oldStill, "ancient DLQ row should be swept");
            Assert.True(freshStill, "recent DLQ row should survive");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM wolverine.wolverine_dead_letters WHERE id IN ({0}, {1})",
                oldId, freshId);
        }
    }

    [Fact]
    public async Task Sweep_PlatformAnswerDedup_UsesShortCleanupWindow_NotRetentionDays()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var configured = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;

        // Default cleanup window is 1h; seed one 30min-old row (must survive) and
        // one 2h-old row (must be deleted). The 2h-old row is well inside the 7d
        // global retention cutoff, proving the sweep now uses the dedicated knob.
        var staleOffset = TimeSpan.FromHours(2);
        var freshOffset = TimeSpan.FromMinutes(30);
        Assert.True(staleOffset > configured.PlatformAnswerDedupCleanupAfter);
        Assert.True(freshOffset < configured.PlatformAnswerDedupCleanupAfter);

        var now = DateTimeOffset.UtcNow;
        var stale = PlatformAnswerDedup.Stamp(UniquePhone(), 0xABCDEF01L, now - staleOffset);
        var fresh = PlatformAnswerDedup.Stamp(UniquePhone(), 0x12345678L, now - freshOffset);
        db.PlatformAnswerDedup.AddRange(stale, fresh);
        await db.SaveChangesAsync();

        try
        {
            var sweeper = Resolve(scope.ServiceProvider);
            var result = await sweeper.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CountsByTable[RetentionTableKeys.PlatformAnswerDedup]);
            Assert.False(await db.PlatformAnswerDedup.AsNoTracking()
                .AnyAsync(d => d.Phone == stale.Phone && d.QuestionHash == stale.QuestionHash));
            Assert.True(await db.PlatformAnswerDedup.AsNoTracking()
                .AnyAsync(d => d.Phone == fresh.Phone && d.QuestionHash == fresh.QuestionHash));
        }
        finally
        {
            await db.PlatformAnswerDedup
                .Where(d => d.Phone == stale.Phone || d.Phone == fresh.Phone)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Sweep_PendingClaimed_EmitsSweptMetric_WithOpUpdateTag()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var now = DateTimeOffset.UtcNow;
        var stale = now - opts.PendingFeedbackClaimAfter - TimeSpan.FromHours(1);

        var (_, staleMatch) = await SeedRequestAndMatchAsync(db, stale);
        var stalePending = MatchFeedback.CreatePending(
            staleMatch.Id, staleMatch.RequestId, FeedbackStep.DidYouFind, stale);
        db.MatchFeedback.Add(stalePending);
        await db.SaveChangesAsync();

        var captured = new List<(long Value, string Table, string Op)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HookMetrics.MeterName
                && instrument.Name == "hook.retention.swept.total")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            string table = string.Empty, op = string.Empty;
            foreach (var kv in tags)
            {
                if (kv.Key == "table") table = kv.Value?.ToString() ?? string.Empty;
                if (kv.Key == "op") op = kv.Value?.ToString() ?? string.Empty;
            }
            captured.Add((measurement, table, op));
        });
        listener.Start();

        var sweeper = Resolve(scope.ServiceProvider);
        await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Contains(captured, m =>
            m.Table == RetentionTableKeys.MatchFeedbackPendingClaimed
            && m.Op == RetentionOps.Update
            && m.Value >= 1);
    }

    [Fact]
    public async Task Sweep_DisabledByConfig_DoesNothing()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, _) = Boundaries(opts);

        var phone = UniquePhone();
        var oldContact = WhatsappContact.Recorded(phone, old);
        db.WhatsappContacts.Add(oldContact);
        await db.SaveChangesAsync();

        try
        {
            var disabledOpts = Options.Create(new RetentionOptions { Enabled = false });
            var sweeper = new RetentionSweeper(
                db, disabledOpts,
                TimeProvider.System,
                NullLogger<RetentionSweeper>.Instance);

            var result = await sweeper.RunOnceAsync(CancellationToken.None);

            Assert.Empty(result.CountsByTable);
            Assert.True(await db.WhatsappContacts.AsNoTracking().AnyAsync(c => c.Phone == phone));
        }
        finally
        {
            // Always remove the seeded row, even if assertions throw, so later tests
            // do not see stale data from this fixture-shared container.
            db.WhatsappContacts.Remove(oldContact);
            await db.SaveChangesAsync();
        }
    }
}
