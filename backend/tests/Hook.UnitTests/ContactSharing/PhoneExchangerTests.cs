using Hook.Features.ContactSharing.Events;
using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ProviderEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;
using ProviderRepoIface = Hook.Features.ProviderAvailability.AvailabilityAggregate.IProviderAvailabilityRepository;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.ContactSharing;

public class PhoneExchangerTests
{
    [Fact]
    public async Task TryExchange_NoMatch_InvalidData()
    {
        var deps = new Deps();
        var outcome = await deps.Build().TryExchangeAsync(Guid.NewGuid());
        Assert.Equal(ExchangeOutcome.InvalidData, outcome);
    }

    [Fact]
    public async Task TryExchange_RequestMissing_RequestMissing()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Requests.Stored.Remove(match.RequestId);
        var outcome = await deps.Build().TryExchangeAsync(match.Id);
        Assert.Equal(ExchangeOutcome.RequestMissing, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderMissing_ProviderMissing()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Providers.Stored.Remove(match.ProviderPhone);
        var outcome = await deps.Build().TryExchangeAsync(match.Id);
        Assert.Equal(ExchangeOutcome.ProviderMissing, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderTtlElapsed_ProviderExpired()
    {
        var deps = new Deps();
        var match = SeedMatch(deps, providerExpired: true);
        var outcome = await deps.Build().TryExchangeAsync(match.Id);
        Assert.Equal(ExchangeOutcome.ProviderExpired, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderTtlEqualsNow_ProviderExpired()
    {
        var deps = new Deps();
        var match = SeedMatch(deps, providerExpiresAtNow: true);
        var outcome = await deps.Build().TryExchangeAsync(match.Id);
        Assert.Equal(ExchangeOutcome.ProviderExpired, outcome);
    }

    [Fact]
    public async Task TryExchange_AlreadySharedRePick_ResendsPhone()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        match.ContactShared = true;
        match.PickedAt = DateTimeOffset.UtcNow;

        var outcome = await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(ExchangeOutcome.AlreadyShared, outcome);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Empty(deps.Events.Published);
    }

    [Fact]
    public async Task TryExchange_AlreadyRoutedRePick_SendsNotice()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        match.PickedAt = DateTimeOffset.UtcNow;

        var outcome = await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(ExchangeOutcome.AlreadyRouted, outcome);
        Assert.Single(deps.Whatsapp.Sent);
        Assert.Contains("chat", deps.Whatsapp.Sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryExchange_RaceLost_NoSendsNoPublishes()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);
        deps.Matches.ClaimResult = false;

        var outcome = await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(ExchangeOutcome.RaceLost, outcome);
        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Events.Published);
    }

    [Fact]
    public async Task TryExchange_BothConsent_ClaimSucceeds_Exchanged()
    {
        var deps = new Deps();
        var match = SeedMatch(deps, sharePhone: true, providerConsent: true);

        var outcome = await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(ExchangeOutcome.Exchanged, outcome);
        Assert.Equal(2, deps.Whatsapp.Sent.Count);
        Assert.Single(deps.Events.Published);
        Assert.IsType<ContactExchanged>(deps.Events.Published[0]);
        Assert.True(deps.Matches.LastClaim?.RevealContact);
    }

    [Fact]
    public async Task TryExchange_NoBilateralConsent_RoutesToChat()
    {
        var deps = new Deps();
        var match = SeedMatch(deps, sharePhone: false, providerConsent: true);

        var outcome = await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(ExchangeOutcome.RoutedToChat, outcome);
        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Single(deps.Events.Published);
        Assert.IsType<ChatRoutingRequested>(deps.Events.Published[0]);
        Assert.False(deps.Matches.LastClaim?.RevealContact);
    }

    [Fact]
    public async Task TryExchange_PassesCallerClientPhoneToClaim()
    {
        var deps = new Deps();
        var match = SeedMatch(deps);

        await deps.Build().TryExchangeAsync(match.Id);

        Assert.Equal(deps.Requests.Stored[match.RequestId].ClientPhone, deps.Matches.LastClaim?.CallerClientPhone);
    }

    [Fact]
    public async Task TryExchange_BadPhoneInRequest_InvalidData()
    {
        var deps = new Deps();
        var match = SeedMatch(deps, clientPhone: "not-a-phone");
        var outcome = await deps.Build().TryExchangeAsync(match.Id);
        Assert.Equal(ExchangeOutcome.InvalidData, outcome);
    }

    private static MatchEntity SeedMatch(
        Deps deps,
        bool providerExpired = false,
        bool providerExpiresAtNow = false,
        bool sharePhone = true,
        bool providerConsent = true,
        string clientPhone = "+2203339999")
    {
        var providerPhone = "+2203331234";
        var match = new MatchEntity
        {
            RequestId = Guid.NewGuid(),
            ProviderPhone = providerPhone,
            ServiceSlug = "plumbing"
        };
        deps.Matches.Stored[match.Id] = match;

        var request = ServiceRequestEntity.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            "req-test", 5.0, DateTimeOffset.UtcNow, sharePhone);
        deps.Requests.Stored[match.RequestId] = request;

        var now = deps.Clock.GetUtcNow();
        var expiresAt = providerExpired ? now.AddHours(-1)
                      : providerExpiresAtNow ? now
                      : now.AddHours(24);
        deps.Providers.Stored[providerPhone] = ProviderEntity.Register(
            providerPhone, new[] { "plumbing" },
            new Location(13.45, -16.6), "Banjul",
            providerConsent, expiresAt - now, now);
        return match;
    }

    private sealed class Deps
    {
        public FakeMatchRepository Matches { get; } = new();
        public FakeRequestRepository Requests { get; } = new();
        public FakeProviderRepository Providers { get; } = new();
        public FakeWhatsapp Whatsapp { get; } = new();
        public FakeEventPublisher Events { get; } = new();
        public TimeProvider Clock { get; } = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-07T12:00:00Z"));

        public PhoneExchanger Build() => new(
            Matches, Requests, Providers, Whatsapp, Events, Clock,
            NullLogger<PhoneExchanger>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeMatchRepository : IMatchRepository
    {
        public Dictionary<Guid, MatchEntity> Stored { get; } = new();
        public bool ClaimResult { get; set; } = true;
        public PickClaim? LastClaim { get; private set; }

        public Task<MatchEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var m) ? m : null);
        public Task<IReadOnlyList<MatchEntity>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MatchEntity>>(Array.Empty<MatchEntity>());
        public Task AddAsync(MatchEntity match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<MatchEntity> matches, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default)
        {
            LastClaim = claim;
            if (ClaimResult && Stored.TryGetValue(claim.MatchId, out var m))
            {
                m.ContactShared = claim.RevealContact;
                m.PickedAt = claim.Now;
            }
            return Task.FromResult(ClaimResult);
        }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRequestRepository : IServiceRequestRepository
    {
        public Dictionary<Guid, ServiceRequestEntity> Stored { get; } = new();
        public Task<ServiceRequestEntity?> GetActiveByClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<ServiceRequestEntity?>(null);
        public Task<ServiceRequestEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var r) ? r : null);
        public Task AddAsync(ServiceRequestEntity request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProviderRepository : ProviderRepoIface
    {
        public Dictionary<string, ProviderEntity> Stored { get; } = new();
        public Task<ProviderEntity?> GetAsync(string phone, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(phone, out var p) ? p : null);
        public Task AddAsync(ProviderEntity availability, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string phone, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWhatsapp : IWhatsappClient
    {
        public List<(PhoneNumber To, string Body)> Sent { get; } = new();
        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            Sent.Add((to, body));
            return Task.FromResult("msg-1");
        }
    }

    private sealed class FakeEventPublisher : IEventPublisher
    {
        public List<object> Published { get; } = new();
        public Task PublishAsync<T>(T message, CancellationToken ct = default)
        {
            Published.Add(message!);
            return Task.CompletedTask;
        }
    }
}
