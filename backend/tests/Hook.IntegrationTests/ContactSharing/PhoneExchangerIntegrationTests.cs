using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.ContactSharing;

[Collection("Pipeline-4")]
public sealed class PhoneExchangerIntegrationTests : PipelineTestBase
{
    public PhoneExchangerIntegrationTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task TryClaimPickAsync_WrongCallerClientPhone_ReturnsFalse()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var (request, match, _) = await SeedAsync(db, providerConsent: true, ttlHours: 24);

        var claimed = await repo.TryClaimPickAsync(
            new PickClaim(match.Id, "+2200000000", RevealContact: true, DateTimeOffset.UtcNow));
        claimed.ShouldBeFalse("a forged client phone must not be able to claim someone else's match");

        await using var verify = _fx.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<HookDbContext>();
        var fresh = await verifyDb.Matches.AsNoTracking().FirstAsync(m => m.Id == match.Id);
        fresh.PickedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryClaimPickAsync_ProviderConsentRevokedMidFlight_ReturnsFalse()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var (request, match, _) = await SeedAsync(db, providerConsent: true, ttlHours: 24);

        // Simulate the provider revoking consent between PhoneExchanger's read and the
        // atomic claim. The folded predicate must reject because ShareContact=false.
        await db.ProviderAvailabilities
            .Where(p => p.Phone == match.ProviderPhone)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ShareContact, false));

        var claimed = await repo.TryClaimPickAsync(
            new PickClaim(match.Id, request.ClientPhone, RevealContact: true, DateTimeOffset.UtcNow));
        claimed.ShouldBeFalse("provider consent must be re-checked atomically with the claim");
    }

    [Fact]
    public async Task TryClaimPickAsync_ProviderExpiredAtClaimTime_ReturnsFalse()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var (request, match, _) = await SeedAsync(db, providerConsent: true, ttlHours: 24);

        // Force the provider's TTL into the past directly.
        await db.ProviderAvailabilities
            .Where(p => p.Phone == match.ProviderPhone)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));

        var claimed = await repo.TryClaimPickAsync(
            new PickClaim(match.Id, request.ClientPhone, RevealContact: true, DateTimeOffset.UtcNow));
        claimed.ShouldBeFalse();
    }

    [Fact]
    public async Task TryClaimPickAsync_ChatRoutePath_DoesNotRequireProviderConsent()
    {
        // RevealContact=false means the chat-routing path: provider consent is not
        // re-checked because we are not sending the phone — the chat link is fine.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var (request, match, _) = await SeedAsync(db, providerConsent: false, ttlHours: 24);

        var claimed = await repo.TryClaimPickAsync(
            new PickClaim(match.Id, request.ClientPhone, RevealContact: false, DateTimeOffset.UtcNow));
        claimed.ShouldBeTrue();
    }

    private static async Task<(ServiceRequest Request, Match Match, ProviderAvailability Provider)> SeedAsync(
        HookDbContext db, bool providerConsent, double ttlHours)
    {
        var clientPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}";
        var providerPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}";
        var now = DateTimeOffset.UtcNow;

        var request = ServiceRequest.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            $"req-{Guid.NewGuid()}", 5.0, now, sharePhoneNumber: true);
        db.ServiceRequests.Add(request);

        var provider = ProviderAvailability.Register(
            providerPhone, new[] { "plumbing" },
            new Location(13.45, -16.6), "Banjul",
            providerConsent, TimeSpan.FromHours(ttlHours), now);
        db.ProviderAvailabilities.Add(provider);

        var match = new Match
        {
            RequestId = request.Id,
            ProviderPhone = providerPhone,
            ServiceSlug = "plumbing"
        };
        db.Matches.Add(match);

        await db.SaveChangesAsync();
        return (request, match, provider);
    }
}
