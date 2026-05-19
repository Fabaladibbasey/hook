using System.Globalization;
using Hook.Features.ChatPrivacyRouting.RouteMatch;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Wolverine;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.UnitTests.ChatPrivacyRouting;

[CollectionDefinition(nameof(CultureSensitiveCollection), DisableParallelization = true)]
public sealed class CultureSensitiveCollection { }

[Collection(nameof(CultureSensitiveCollection))]
public class ChatRoutingRequestedHandlerTests
{
    private const string ClientPhone = "+2203339999";
    private const string ProviderPhone = "+2203331234";
    private const string MaskedProvider = "+220***34";
    private const string Slug = "plumbing";
    private const string Address = "Banjul";
    private const double Lat = 13.45;
    private const double Lon = -16.6;
    private const int MatchPosition = 2;

    private readonly Dictionary<Guid, MatchEntity> _matches = new();
    private readonly List<ChatSession> _sessions = new();
    private readonly List<(string To, string Body)> _sent = new();

    private readonly Mock<IMatchRepository> _matchesMock;
    private readonly Mock<IChatRepository> _chatsMock;
    private readonly Mock<IMessageBus> _busMock;
    private readonly TimeProvider _clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z"));
    private readonly IOptions<ChatOptions> _chatOptions =
        Options.Create(new ChatOptions { PublicChatBaseUrl = "https://hook.test" });

    public ChatRoutingRequestedHandlerTests()
    {
        _matchesMock = new Mock<IMatchRepository>();
        _matchesMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _matches.TryGetValue(id, out var m) ? m : null);
        _matchesMock.Setup(x => x.TryClaimChatRoutingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _chatsMock = new Mock<IChatRepository>();
        _chatsMock.Setup(x => x.AddSessionAsync(It.IsAny<ChatSession>(), It.IsAny<CancellationToken>()))
            .Callback<ChatSession, CancellationToken>((s, _) => _sessions.Add(s))
            .Returns(Task.CompletedTask);
        _chatsMock.Setup(x => x.AddParticipantsAsync(It.IsAny<IEnumerable<ChatParticipant>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _busMock = new Mock<IMessageBus>();
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) =>
            {
                var req = (SendWhatsAppTextRequested)msg;
                _sent.Add((req.To.Value, req.Text));
            })
            .Returns(ValueTask.CompletedTask);
    }

    private MatchEntity SeedMatch()
    {
        var match = MatchEntity.Create(Guid.NewGuid(), ProviderPhone, Slug, 0, 0, _clock.GetUtcNow());
        _matches[match.Id] = match;
        return match;
    }

    private ChatRoutingRequestedHandler Build()
    {
        var factory = new ChatSessionFactory(_chatsMock.Object, _chatOptions, _clock);
        return new ChatRoutingRequestedHandler(factory, _matchesMock.Object,
            NullLogger<ChatRoutingRequestedHandler>.Instance);
    }

    [Fact]
    public async Task Handle_BothHidConsent_NeitherMessageBlamesTheOtherParty()
    {
        var match = SeedMatch();

        await Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), _busMock.Object, CancellationToken.None);

        var client = _sent.Single(s => s.To == ClientPhone);
        var provider = _sent.Single(s => s.To == ProviderPhone);
        Assert.StartsWith($"Match #{MatchPosition} ({MaskedProvider}): your private chat is ready. Open: ", client.Body);
        Assert.Contains($"{Slug} client at {Address} (https://maps.google.com/?q=13.45,-16.6) wants to chat. Open: ", provider.Body);
        Assert.DoesNotContain("prefers", client.Body);
        Assert.DoesNotContain("prefers", provider.Body);
    }

    [Fact]
    public async Task Handle_OnlyClientConsented_OtherPartyToClient_NeutralToProvider()
    {
        var match = SeedMatch();

        await Build().Handle(MakeEvt(match.Id, clientConsented: true, providerConsented: false), _busMock.Object, CancellationToken.None);

        var client = _sent.Single(s => s.To == ClientPhone);
        var provider = _sent.Single(s => s.To == ProviderPhone);
        Assert.StartsWith($"Match #{MatchPosition} ({MaskedProvider}): the other party prefers a private chat. Open: ", client.Body);
        Assert.Contains("wants to chat", provider.Body);
        Assert.Contains("https://maps.google.com/?q=13.45,-16.6", provider.Body);
    }

    [Fact]
    public async Task Handle_OnlyProviderConsented_NeutralToClient_OtherPartyToProvider()
    {
        var match = SeedMatch();

        await Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: true), _busMock.Object, CancellationToken.None);

        var client = _sent.Single(s => s.To == ClientPhone);
        var provider = _sent.Single(s => s.To == ProviderPhone);
        Assert.StartsWith($"Match #{MatchPosition} ({MaskedProvider}): your private chat is ready. Open: ", client.Body);
        Assert.Contains("prefers a private chat", provider.Body);
        Assert.Contains("https://maps.google.com/?q=13.45,-16.6", provider.Body);
    }

    [Fact]
    public async Task Handle_MapsUrlUsesInvariantCulture_EvenUnderGermanLocale()
    {
        var original = CultureInfo.DefaultThreadCurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
        try
        {
            var match = SeedMatch();

            await Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), _busMock.Object, CancellationToken.None);

            var provider = _sent.Single(s => s.To == ProviderPhone);
            Assert.Contains("?q=13.45,-16.6", provider.Body);
            Assert.DoesNotContain("?q=13,45", provider.Body);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = original;
        }
    }

    [Fact]
    public async Task Handle_MatchAlreadyHasChatId_DoesNotSendOrCreateLinks()
    {
        var match = SeedMatch();
        match.ClaimForChat(Guid.NewGuid());

        await Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), _busMock.Object, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task Handle_MatchMissing_DoesNotSendOrCreateLinks()
    {
        await Build().Handle(MakeEvt(Guid.NewGuid(), clientConsented: false, providerConsented: false), _busMock.Object, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Empty(_sessions);
    }

    private static ChatRoutingRequested MakeEvt(Guid matchId, bool clientConsented, bool providerConsented) =>
        new(matchId, Guid.NewGuid(), ClientPhone, ProviderPhone, clientConsented, providerConsented, Address, Lat, Lon, MatchPosition);
}
