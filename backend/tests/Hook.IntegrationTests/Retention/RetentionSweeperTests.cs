using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.MetaTemplates;
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

    private static string UniquePhone() => $"+220{Guid.NewGuid().ToString("N")[..8]}";

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
        // EF model — it is swept via raw SQL. Exempt that one key from the EF-mapped check.
        var rawSweeps = new HashSet<string>(StringComparer.Ordinal)
        {
            RetentionTableKeys.WolverineDeadLetters,
        };

        foreach (var key in result.DeletedByTable.Keys)
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
        var oldGeo = new GeocodeCacheEntry
        {
            Key = oldGeoKey,
            Latitude = 13.45,
            Longitude = -16.6,
            FormattedAddress = "Banjul",
            Provider = "test",
            FetchedAt = old
        };
        var newGeo = new GeocodeCacheEntry
        {
            Key = freshGeoKey,
            Latitude = 13.45,
            Longitude = -16.6,
            FormattedAddress = "Banjul",
            Provider = "test",
            FetchedAt = fresh
        };
        db.GeocodeCache.AddRange(oldGeo, newGeo);

        var oldContact = new WhatsappContact { Phone = UniquePhone(), LastInboundAt = old };
        var freshContact = new WhatsappContact { Phone = UniquePhone(), LastInboundAt = fresh };
        db.WhatsappContacts.AddRange(oldContact, freshContact);

        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.DeletedByTable[RetentionTableKeys.ChatSessions]);
        Assert.Equal(1, result.DeletedByTable[RetentionTableKeys.ServiceRequests]);
        Assert.Equal(1, result.DeletedByTable[RetentionTableKeys.GeocodeCache]);
        Assert.Equal(1, result.DeletedByTable[RetentionTableKeys.WhatsappContacts]);
        Assert.True(result.DeletedByTable.ContainsKey(RetentionTableKeys.MatchFeedback));
        Assert.False(result.DeletedByTable.ContainsKey("provider_stats"));

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
        oldAnswered.Resolve(FeedbackAnswer.Yes, ancient + TimeSpan.FromHours(2));
        var oldPending = MatchFeedback.CreatePending(
            oldMatch2.Id, oldMatch2.RequestId, FeedbackStep.DidYouFind, old);
        var recent = MatchFeedback.CreatePending(
            freshMatch.Id, freshMatch.RequestId, FeedbackStep.JobCompleted, fresh);
        recent.Resolve(FeedbackAnswer.Yes, fresh);
        var phone = UniquePhone();
        var stats = ProviderStats.Initial(phone, ancient);

        db.MatchFeedback.AddRange(oldAnswered, oldPending, recent);
        db.ProviderStats.Add(stats);
        await db.SaveChangesAsync();

        var sweeper = Resolve(scope.ServiceProvider);
        var result = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.DeletedByTable[RetentionTableKeys.MatchFeedback]);
        Assert.False(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == oldAnswered.Id));
        Assert.False(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == oldPending.Id));
        Assert.True(await db.MatchFeedback.AsNoTracking().AnyAsync(f => f.Id == recent.Id));
        Assert.True(await db.ProviderStats.AsNoTracking().AnyAsync(s => s.ProviderPhone == phone));
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
        var feedback = MatchFeedback.CreatePending(match.Id, match.RequestId, FeedbackStep.DidYouFind, fresh);
        feedback.Resolve(FeedbackAnswer.Yes, fresh);
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
        // sent_at is the timestamptz axis the sweeper compares against;
        // received_at is a varchar holding the listener URI and is irrelevant here.
        const string Insert =
            "INSERT INTO wolverine.wolverine_dead_letters (id, message_type, body, sent_at) " +
            "VALUES ({0}, 'TestMsg', E'\\\\x00', {1})";
        await db.Database.ExecuteSqlRawAsync(Insert, oldId, ancient);
        await db.Database.ExecuteSqlRawAsync(Insert, freshId, fresh);

        try
        {
            var sweeper = Resolve(scope.ServiceProvider);
            var result = await sweeper.RunOnceAsync(CancellationToken.None);

            Assert.True(result.DeletedByTable[RetentionTableKeys.WolverineDeadLetters] >= 1,
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
    public async Task Sweep_DisabledByConfig_DoesNothing()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        var (old, _) = Boundaries(opts);

        var phone = UniquePhone();
        var oldContact = new WhatsappContact { Phone = phone, LastInboundAt = old };
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

            Assert.Empty(result.DeletedByTable);
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
