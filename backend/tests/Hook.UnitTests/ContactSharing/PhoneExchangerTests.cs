using Hook.Features.ChatPrivacyRouting.RouteMatch;
using Hook.Features.ContactSharing.Events;
using Hook.Features.ContactSharing.ExchangePhones;
using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Core;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ProviderEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;
using ProviderRepoIface = Hook.Features.ProviderAvailability.AvailabilityAggregate.IProviderAvailabilityRepository;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.ContactSharing;

public class PhoneExchangerTests
{
    private readonly Dictionary<Guid, MatchEntity> _matches = [];
    private readonly Dictionary<Guid, ServiceRequestEntity> _requests = [];
    private readonly Dictionary<string, ProviderEntity> _providers = [];
    private readonly List<(PhoneNumber To, string Body)> _sent = [];
    private readonly List<object> _published = [];

    private readonly Mock<IMatchRepository> _matchesMock = new();
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<ProviderRepoIface> _providersMock = new();
    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<IEventPublisher> _eventsMock = new();
    private readonly TimeProvider _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

    private bool _claimResult = true;
    private PickClaim? _lastClaim;

    public PhoneExchangerTests()
    {
        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.TryClaimPickAsync(It.IsAny<PickClaim>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PickClaim claim, CancellationToken _) =>
            {
                _lastClaim = claim;
                // Production: TryClaimPickAsync is a DB UPDATE; PhoneExchanger then
                // calls match.ClaimForPickup(...) to mirror the committed state on the
                // tracked aggregate. This mock leaves ContactShared/PickedAt untouched
                // — PhoneExchanger does the sync, not the repository.
                return _claimResult;
            });

        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _requests.TryGetValue(id, out var r) ? r : null);

        _providersMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string phone, CancellationToken _) =>
                _providers.TryGetValue(phone, out var p) ? p : null);

        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) =>
            {
                var req = (SendWhatsAppTextCommand)msg;
                _sent.Add((req.To, req.Text));
            })
            .Returns(ValueTask.CompletedTask);

        _eventsMock.Setup(x => x.PublishAsync(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(inv => _published.Add(inv.Arguments[0])))
            .Returns(Task.CompletedTask);
    }

    private PhoneExchanger Build() => new(
        _matchesMock.Object, _requestsMock.Object, _providersMock.Object,
        _busMock.Object, _eventsMock.Object, _clock,
        NullLogger<PhoneExchanger>.Instance);

    private MatchEntity SeedMatch(
        DateTimeOffset? providerExpiresAt = null,
        bool sharePhone = true,
        bool providerConsent = true,
        string clientPhone = "+2203339999",
        string description = "")
    {
        var providerPhone = "+2203331234";
        var match = MatchEntity.Create(Guid.NewGuid(), providerPhone, "plumbing", 0, 0, _clock.GetUtcNow());
        _matches[match.Id] = match;

        var request = ServiceRequestEntity.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            description, 5.0, DateTimeOffset.UtcNow, sharePhone);
        _requests[match.RequestId] = request;

        var now = _clock.GetUtcNow();
        var expiresAt = providerExpiresAt ?? now.AddHours(24);
        _providers[providerPhone] = ProviderEntity.Register(
            providerPhone, ["plumbing"],
            new Location(13.45, -16.6), "Banjul",
            providerConsent, expiresAt - now, now);
        return match;
    }

    [Fact]
    public async Task TryExchange_NoMatch_InvalidData()
    {
        var outcome = await Build().TryExchangeAsync(Guid.NewGuid(), 1);
        Assert.Equal(ExchangeOutcome.InvalidData, outcome);
    }

    [Fact]
    public async Task TryExchange_RequestMissing_RequestMissing()
    {
        var match = SeedMatch();
        _requests.Remove(match.RequestId);
        var outcome = await Build().TryExchangeAsync(match.Id, 1);
        Assert.Equal(ExchangeOutcome.RequestMissing, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderMissing_ProviderMissing()
    {
        var match = SeedMatch();
        _providers.Remove(match.ProviderPhone);
        var outcome = await Build().TryExchangeAsync(match.Id, 1);
        Assert.Equal(ExchangeOutcome.ProviderMissing, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderTtlElapsed_ProviderExpired()
    {
        var match = SeedMatch(providerExpiresAt: _clock.GetUtcNow().AddHours(-1));
        var outcome = await Build().TryExchangeAsync(match.Id, 1);
        Assert.Equal(ExchangeOutcome.ProviderExpired, outcome);
    }

    [Fact]
    public async Task TryExchange_ProviderTtlEqualsNow_ProviderExpired()
    {
        var match = SeedMatch(providerExpiresAt: _clock.GetUtcNow());
        var outcome = await Build().TryExchangeAsync(match.Id, 1);
        Assert.Equal(ExchangeOutcome.ProviderExpired, outcome);
    }

    [Fact]
    public async Task TryExchange_AlreadySharedRePick_ResendsPhone_PrefixedByMatchPosition()
    {
        var match = SeedMatch();
        match.ClaimForPickup(true, DateTimeOffset.UtcNow);

        var outcome = await Build().TryExchangeAsync(match.Id, 7);

        Assert.Equal(ExchangeOutcome.AlreadyShared, outcome);
        Assert.Single(_sent);
        Assert.StartsWith("Match #7: ", _sent[0].Body);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task TryExchange_AlreadyRoutedRePick_SendsNotice_PrefixedByMatchPositionAndMaskedPhone()
    {
        var match = SeedMatch();
        match.ClaimForPickup(false, DateTimeOffset.UtcNow);

        var outcome = await Build().TryExchangeAsync(match.Id, 3);

        Assert.Equal(ExchangeOutcome.AlreadyRouted, outcome);
        Assert.Single(_sent);
        Assert.StartsWith("Match #3 (+220***34): ", _sent[0].Body);
        Assert.Contains("chat", _sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryExchange_RaceLost_NoSendsNoPublishes()
    {
        var match = SeedMatch();
        _claimResult = false;

        var outcome = await Build().TryExchangeAsync(match.Id, 1);

        Assert.Equal(ExchangeOutcome.RaceLost, outcome);
        Assert.Empty(_sent);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task TryExchange_BothConsent_ClaimSucceeds_Exchanged()
    {
        var match = SeedMatch(sharePhone: true, providerConsent: true);

        var outcome = await Build().TryExchangeAsync(match.Id, 2);

        Assert.Equal(ExchangeOutcome.Exchanged, outcome);
        Assert.Equal(2, _sent.Count);
        var clientNotice = _sent.Single(s => s.To.Value == "+2203339999");
        Assert.StartsWith("Match #2: provider for plumbing: ", clientNotice.Body);
        var raised = match.DequeueEvents();
        Assert.Single(raised);
        Assert.IsType<ContactExchangedEvent>(raised[0]);
        Assert.True(_lastClaim?.RevealContact);
    }

    [Fact]
    public async Task TryExchange_NoBilateralConsent_RoutesToChat_CarriesMatchPosition()
    {
        var match = SeedMatch(sharePhone: false, providerConsent: true);

        var outcome = await Build().TryExchangeAsync(match.Id, 4);

        Assert.Equal(ExchangeOutcome.RoutedToChat, outcome);
        Assert.Empty(_sent);
        Assert.Single(_published);
        var evt = Assert.IsType<RouteMatchToChatCommand>(_published[0]);
        Assert.False(evt.ClientConsented);
        Assert.True(evt.ProviderConsented);
        Assert.Equal("Banjul", evt.RequesterAddress);
        Assert.Equal(13.45, evt.RequesterLatitude);
        Assert.Equal(-16.6, evt.RequesterLongitude);
        Assert.Equal(4, evt.MatchPosition);
        Assert.False(_lastClaim?.RevealContact);
    }

    [Fact]
    public async Task TryExchange_BothHidConsent_RoutesToChatWithBothFlagsFalse()
    {
        var match = SeedMatch(sharePhone: false, providerConsent: false);

        var outcome = await Build().TryExchangeAsync(match.Id, 1);

        Assert.Equal(ExchangeOutcome.RoutedToChat, outcome);
        Assert.Empty(_sent);
        var evt = Assert.IsType<RouteMatchToChatCommand>(_published[0]);
        Assert.False(evt.ClientConsented);
        Assert.False(evt.ProviderConsented);
        Assert.Equal("Banjul", evt.RequesterAddress);
        Assert.Equal(13.45, evt.RequesterLatitude);
        Assert.Equal(-16.6, evt.RequesterLongitude);
    }

    [Fact]
    public async Task TryExchange_OnlyProviderHidConsent_RoutesToChatWithProviderFalse()
    {
        var match = SeedMatch(sharePhone: true, providerConsent: false);

        var outcome = await Build().TryExchangeAsync(match.Id, 1);

        Assert.Equal(ExchangeOutcome.RoutedToChat, outcome);
        var evt = Assert.IsType<RouteMatchToChatCommand>(_published[0]);
        Assert.True(evt.ClientConsented);
        Assert.False(evt.ProviderConsented);
    }

    [Fact]
    public async Task TryExchange_PassesCallerClientPhoneToClaim()
    {
        var match = SeedMatch();

        await Build().TryExchangeAsync(match.Id, 1);

        Assert.Equal(_requests[match.RequestId].ClientPhone, _lastClaim?.CallerClientPhone);
    }

    [Fact]
    public async Task TryExchange_BadPhoneInRequest_InvalidData()
    {
        var match = SeedMatch(clientPhone: "not-a-phone");
        var outcome = await Build().TryExchangeAsync(match.Id, 1);
        Assert.Equal(ExchangeOutcome.InvalidData, outcome);
    }

    [Fact]
    public async Task TryExchange_BothConsent_NonBlankDescription_ProviderMessageHasForwardedSegment()
    {
        var match = SeedMatch(sharePhone: true, providerConsent: true, description: "leaking pipe under sink");

        await Build().TryExchangeAsync(match.Id, 1);

        var providerMsg = _sent.Single(s => s.To.Value == "+2203331234").Body;
        providerMsg.ShouldContain("— client message (forwarded, not verified) —");
        providerMsg.ShouldContain("leaking pipe under sink");
        providerMsg.ShouldContain("— end client message —");
    }

    [Fact]
    public async Task TryExchange_BothConsent_EmptyDescription_ProviderMessageHasNoForwardedSegment()
    {
        var match = SeedMatch(sharePhone: true, providerConsent: true, description: string.Empty);

        await Build().TryExchangeAsync(match.Id, 1);

        var providerMsg = _sent.Single(s => s.To.Value == "+2203331234").Body;
        providerMsg.ShouldNotContain("— client message");
    }

    [Fact]
    public async Task TryExchange_BothConsent_NonBlankDescription_ClientResendNotificationHasNoForwardedSegment()
    {
        var match = SeedMatch(sharePhone: true, providerConsent: true, description: "leaking pipe");

        await Build().TryExchangeAsync(match.Id, 1);

        var clientMsg = _sent.Single(s => s.To.Value == "+2203339999").Body;
        clientMsg.ShouldNotContain("— client message");
        clientMsg.ShouldNotContain("leaking pipe");
    }

    [Fact]
    public async Task TryExchange_NoBilateralConsent_PublishesRouteMatchToChatCommandCarryingDescription()
    {
        var match = SeedMatch(sharePhone: false, providerConsent: true, description: "kitchen sink leak");

        await Build().TryExchangeAsync(match.Id, 1);

        var evt = Assert.IsType<RouteMatchToChatCommand>(_published[0]);
        evt.Description.ShouldBe("kitchen sink leak");
    }
}
