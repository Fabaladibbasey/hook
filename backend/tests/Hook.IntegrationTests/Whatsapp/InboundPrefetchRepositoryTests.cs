using Hook.Features.Feedback;
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

    private static string UniquePhone() => $"+220{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task GetAllAsync_EmptyState_ReturnsAllNull()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();

        var pre = await prefetch.GetAllAsync(UniquePhone(), default);

        pre.RegistrationDraft.ShouldBeNull();
        pre.ClientDraft.ShouldBeNull();
        pre.AmbiguousDraft.ShouldBeNull();
        pre.PendingFeedback.ShouldBeNull();
        pre.ActiveRequest.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllAsync_AllFiveBucketsSeeded_AllPopulate()
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
        var pre = await prefetch.GetAllAsync(phone, default);

        pre.RegistrationDraft.ShouldNotBeNull();
        pre.ClientDraft.ShouldNotBeNull();
        pre.AmbiguousDraft.ShouldNotBeNull();
        pre.AmbiguousDraft!.OriginalText.ShouldBe("ambiguous original");
        pre.PendingFeedback.ShouldNotBeNull();
        pre.PendingFeedback!.Step.ShouldBe(FeedbackStep.DidYouFind);
        pre.ActiveRequest.ShouldNotBeNull();
        pre.ActiveRequest!.ServiceSlug.ShouldBe("plumbing");
    }

    [Fact]
    public async Task GetAllAsync_OnlyPendingFeedback_OthersStayNull()
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
        var pre = await prefetch.GetAllAsync(phone, default);

        pre.RegistrationDraft.ShouldBeNull();
        pre.ClientDraft.ShouldBeNull();
        pre.AmbiguousDraft.ShouldBeNull();
        pre.ActiveRequest.ShouldBeNull();
        pre.PendingFeedback.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetAllAsync_AnsweredFeedback_NotReturned()
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
            var answered = MatchFeedback.CreatePending(
                match.Id, request.Id, FeedbackStep.DidYouFind, now);
            answered.Resolve(FeedbackAnswer.Yes, now);
            db.MatchFeedback.Add(answered);
            await db.SaveChangesAsync();
        }

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var prefetch = scope.ServiceProvider.GetRequiredService<InboundPrefetchRepository>();
        var pre = await prefetch.GetAllAsync(phone, default);

        pre.PendingFeedback.ShouldBeNull();
        pre.ActiveRequest.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetAllAsync_ActiveRequestIsTracked_CloseAndSaveChangesPersists()
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

            var pre = await prefetch.GetAllAsync(phone, default);
            pre.ActiveRequest.ShouldNotBeNull();
            requestId = pre.ActiveRequest!.Id;

            pre.ActiveRequest.Close();
            await db.SaveChangesAsync();
        }

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var reloaded = await verifyDb.ServiceRequests.FindAsync(requestId);
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(ServiceRequestStatus.Closed);
    }

    [Fact]
    public async Task GetAllAsync_PrefetchedDrafts_AreDetached_ChatSessionStaysTracked()
    {
        // Drafts are AsNoTracking (read-only by callers). The ActiveRequest is
        // tracked on the scoped context because the router calls .Close() and
        // relies on Wolverine AutoApplyTransactions SaveChanges to persist that
        // mutation.
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
        var pre = await prefetch.GetAllAsync(phone, default);

        db2.Entry(pre.RegistrationDraft!).State.ShouldBe(EntityState.Detached);
        db2.Entry(pre.ClientDraft!).State.ShouldBe(EntityState.Detached);
        db2.Entry(pre.AmbiguousDraft!).State.ShouldBe(EntityState.Detached);
        db2.Entry(pre.ActiveRequest!).State.ShouldBe(EntityState.Unchanged);
    }

    [Fact]
    public async Task GetAllAsync_OnlyMatchesOnClientPhone_OtherClientsExcluded()
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
        var pre = await prefetch.GetAllAsync(phoneA, default);

        pre.RegistrationDraft.ShouldBeNull();
        pre.ClientDraft.ShouldBeNull();
        pre.AmbiguousDraft.ShouldBeNull();
        pre.PendingFeedback.ShouldBeNull();
        pre.ActiveRequest.ShouldBeNull();
    }
}
