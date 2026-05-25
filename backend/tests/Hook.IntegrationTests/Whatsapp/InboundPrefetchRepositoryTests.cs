using Hook.Features.Feedback.Models;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Whatsapp;

[Collection("Pipeline-2")]
public sealed class InboundPrefetchRepositoryTests : PipelineTestBase
{
    public InboundPrefetchRepositoryTests(DevPipelineFixture fx) : base(fx) { }

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    [Fact]
    public async Task GetRegistrationDraftAsync_EmptyState_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetRegistrationDraftAsync(UniquePhone(), default)).ShouldBeNull();
    }

    [Fact]
    public async Task GetClientDraftAsync_EmptyState_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetClientDraftAsync(UniquePhone(), default)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAmbiguousDraftAsync_EmptyState_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetAmbiguousDraftAsync(UniquePhone(), default)).ShouldBeNull();
    }

    [Fact]
    public async Task GetPendingFeedbackAsync_EmptyState_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetPendingFeedbackAsync(UniquePhone(), default)).ShouldBeNull();
    }

    [Fact]
    public async Task GetActiveRequestAsync_EmptyState_ReturnsNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetActiveRequestAsync(UniquePhone(), default)).ShouldBeNull();
    }

    [Fact]
    public async Task AllMethods_AllBucketsSeeded_AllPopulate()
    {
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            db.RegistrationDrafts.Add(RegistrationDraft.Start(phone, now));
            db.ClientRequestDrafts.Add(ClientRequestDraft.Start(phone, now));
            db.AmbiguousIntentDrafts.Add(AmbiguousIntentDraft.Start(phone, "ambiguous original", now));

            var request = ServiceRequest.Create(
                phone, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "kitchen sink leak", 5.0, now, sharePhoneNumber: false);
            db.ServiceRequests.Add(request);

            var match = Match.Create(
                request.Id, UniquePhone(), "plumbing", 0.5, 1.0, now);
            db.Matches.Add(match);

            db.MatchFeedback.Add(MatchFeedback.CreatePending(
                match.Id, request.Id, FeedbackStep.DidYouFind, now));

            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetRegistrationDraftAsync(phone, default)).ShouldNotBeNull();
        (await prefetch.GetClientDraftAsync(phone, default)).ShouldNotBeNull();
        var ambig = await prefetch.GetAmbiguousDraftAsync(phone, default);
        ambig.ShouldNotBeNull();
        ambig!.OriginalText.ShouldBe("ambiguous original");
        var pf = await prefetch.GetPendingFeedbackAsync(phone, default);
        pf.ShouldNotBeNull();
        pf!.Step.ShouldBe(FeedbackStep.DidYouFind);
        var active = await prefetch.GetActiveRequestAsync(phone, default);
        active.ShouldNotBeNull();
        active!.ServiceSlug.ShouldBe("plumbing");
    }

    [Fact]
    public async Task GetPendingFeedbackAsync_OnlyPendingFeedback_OthersStayNull()
    {
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            var request = ServiceRequest.Create(
                phone, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "tap leak", 5.0, now, sharePhoneNumber: false);
            request.Close();
            db.ServiceRequests.Add(request);
            var match = Match.Create(request.Id, UniquePhone(), "plumbing", 0, 0, now);
            db.Matches.Add(match);
            db.MatchFeedback.Add(MatchFeedback.CreatePending(
                match.Id, request.Id, FeedbackStep.DidYouFind, now));
            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetRegistrationDraftAsync(phone, default)).ShouldBeNull();
        (await prefetch.GetClientDraftAsync(phone, default)).ShouldBeNull();
        (await prefetch.GetAmbiguousDraftAsync(phone, default)).ShouldBeNull();
        (await prefetch.GetActiveRequestAsync(phone, default)).ShouldBeNull();
        (await prefetch.GetPendingFeedbackAsync(phone, default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task GetPendingFeedbackAsync_AnsweredFeedback_NotReturned()
    {
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            var request = ServiceRequest.Create(
                phone, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "no flow", 5.0, now, sharePhoneNumber: false);
            db.ServiceRequests.Add(request);
            var match = Match.Create(request.Id, UniquePhone(), "plumbing", 0, 0, now);
            db.Matches.Add(match);
            var answered = MatchFeedback.CreatePending(match.Id, request.Id, FeedbackStep.DidYouFind, now);
            answered.ClaimYes(now);
            db.MatchFeedback.Add(answered);
            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetPendingFeedbackAsync(phone, default)).ShouldBeNull();
        (await prefetch.GetActiveRequestAsync(phone, default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task GetActiveRequestAsync_IsTracked_CloseAndSaveChangesPersists()
    {
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            var request = ServiceRequest.Create(
                phone, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "open request", 5.0, now, sharePhoneNumber: false);
            db.ServiceRequests.Add(request);
            await db.SaveChangesAsync();
        }

        Guid requestId;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();
            var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

            var active = await prefetch.GetActiveRequestAsync(phone, default);
            active.ShouldNotBeNull();
            requestId = active!.Id;

            active.Close();
            await db.SaveChangesAsync();
        }

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var reloaded = await verifyDb.ServiceRequests.FindAsync(requestId);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(ServiceRequestStatus.Closed);
    }

    [Fact]
    public async Task PrefetchedDrafts_AreDetached_ActiveRequestStaysTracked()
    {
        // Drafts + pending feedback are AsNoTracking (read-only by callers).
        // The ActiveRequest is tracked on the scoped context because the router
        // calls .Close() and relies on Wolverine AutoApplyTransactions SaveChanges
        // to persist that mutation.
        var phone = UniquePhone();
        var now = DateTimeOffset.UtcNow;
        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            db.RegistrationDrafts.Add(RegistrationDraft.Start(phone, now));
            db.ClientRequestDrafts.Add(ClientRequestDraft.Start(phone, now));
            db.AmbiguousIntentDrafts.Add(AmbiguousIntentDraft.Start(phone, "ambig", now));
            db.ServiceRequests.Add(ServiceRequest.Create(
                phone, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "tracked", 5.0, now, sharePhoneNumber: false));
            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();
        var db2 = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var reg = await prefetch.GetRegistrationDraftAsync(phone, default);
        var client = await prefetch.GetClientDraftAsync(phone, default);
        var ambig = await prefetch.GetAmbiguousDraftAsync(phone, default);
        var active = await prefetch.GetActiveRequestAsync(phone, default);

        db2.Entry(reg!).State.ShouldBe(EntityState.Detached);
        db2.Entry(client!).State.ShouldBe(EntityState.Detached);
        db2.Entry(ambig!).State.ShouldBe(EntityState.Detached);
        db2.Entry(active!).State.ShouldBe(EntityState.Unchanged);
    }

    [Fact]
    public async Task AllMethods_OnlyMatchOnClientPhone_OtherClientsExcluded()
    {
        var phoneA = UniquePhone();
        var phoneB = UniquePhone();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HookDbContext>();
            db.RegistrationDrafts.Add(RegistrationDraft.Start(phoneB, now));
            db.ClientRequestDrafts.Add(ClientRequestDraft.Start(phoneB, now));
            db.AmbiguousIntentDrafts.Add(AmbiguousIntentDraft.Start(phoneB, "for B", now));

            var request = ServiceRequest.Create(
                phoneB, "plumbing",
                new Location(13.45, -16.6), "Banjul",
                "B's request", 5.0, now, sharePhoneNumber: false);
            db.ServiceRequests.Add(request);
            var match = Match.Create(request.Id, UniquePhone(), "plumbing", 0, 0, now);
            db.Matches.Add(match);
            db.MatchFeedback.Add(MatchFeedback.CreatePending(
                match.Id, request.Id, FeedbackStep.DidYouFind, now));
            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        (await prefetch.GetRegistrationDraftAsync(phoneA, default)).ShouldBeNull();
        (await prefetch.GetClientDraftAsync(phoneA, default)).ShouldBeNull();
        (await prefetch.GetAmbiguousDraftAsync(phoneA, default)).ShouldBeNull();
        (await prefetch.GetPendingFeedbackAsync(phoneA, default)).ShouldBeNull();
        (await prefetch.GetActiveRequestAsync(phoneA, default)).ShouldBeNull();
    }
}
