using System.Globalization;
using Hook.Features.ChatPrivacyRouting.RouteMatch;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.UnitTests.ChatPrivacyRouting;

public class ChatRoutingRequestedHandlerTests
{
    private const string ClientPhone = "+2203339999";
    private const string ProviderPhone = "+2203331234";
    private const string Slug = "plumbing";
    private const string Address = "Banjul";
    private const double Lat = 13.45;
    private const double Lon = -16.6;

    [Fact]
    public async Task Handle_BothHidConsent_NeitherMessageBlamesTheOtherParty()
    {
        var deps = new Deps();
        var match = deps.SeedMatch();

        await deps.Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), CancellationToken.None);

        var client = deps.Whatsapp.Sent.Single(s => s.To.Value == ClientPhone);
        var provider = deps.Whatsapp.Sent.Single(s => s.To.Value == ProviderPhone);
        Assert.StartsWith("Your private chat is ready. Open: ", client.Body);
        Assert.Contains($"{Slug} client at {Address} (https://maps.google.com/?q=13.45,-16.6) wants to chat. Open: ", provider.Body);
        Assert.DoesNotContain("prefers", client.Body);
        Assert.DoesNotContain("prefers", provider.Body);
    }

    [Fact]
    public async Task Handle_OnlyClientConsented_OtherPartyToClient_NeutralToProvider()
    {
        var deps = new Deps();
        var match = deps.SeedMatch();

        await deps.Build().Handle(MakeEvt(match.Id, clientConsented: true, providerConsented: false), CancellationToken.None);

        var client = deps.Whatsapp.Sent.Single(s => s.To.Value == ClientPhone);
        var provider = deps.Whatsapp.Sent.Single(s => s.To.Value == ProviderPhone);
        Assert.StartsWith("The other party prefers a private chat. Open: ", client.Body);
        Assert.Contains("wants to chat", provider.Body);
        Assert.Contains("https://maps.google.com/?q=13.45,-16.6", provider.Body);
    }

    [Fact]
    public async Task Handle_OnlyProviderConsented_NeutralToClient_OtherPartyToProvider()
    {
        var deps = new Deps();
        var match = deps.SeedMatch();

        await deps.Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: true), CancellationToken.None);

        var client = deps.Whatsapp.Sent.Single(s => s.To.Value == ClientPhone);
        var provider = deps.Whatsapp.Sent.Single(s => s.To.Value == ProviderPhone);
        Assert.StartsWith("Your private chat is ready. Open: ", client.Body);
        Assert.Contains("prefers a private chat", provider.Body);
        Assert.Contains("https://maps.google.com/?q=13.45,-16.6", provider.Body);
    }

    [Fact]
    public async Task Handle_MapsUrlUsesInvariantCulture_EvenUnderGermanLocale()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var deps = new Deps();
            var match = deps.SeedMatch();

            await deps.Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), CancellationToken.None);

            var provider = deps.Whatsapp.Sent.Single(s => s.To.Value == ProviderPhone);
            Assert.Contains("?q=13.45,-16.6", provider.Body);
            Assert.DoesNotContain("?q=13,45", provider.Body);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Handle_MatchAlreadyHasChatId_DoesNotSendOrCreateLinks()
    {
        var deps = new Deps();
        var match = deps.SeedMatch();
        match.ChatId = Guid.NewGuid();

        await deps.Build().Handle(MakeEvt(match.Id, clientConsented: false, providerConsented: false), CancellationToken.None);

        Assert.Empty(deps.Whatsapp.Sent);
        Assert.Empty(deps.Chats.Sessions);
    }

    private static ChatRoutingRequested MakeEvt(Guid matchId, bool clientConsented, bool providerConsented) =>
        new(matchId, Guid.NewGuid(), ClientPhone, ProviderPhone, clientConsented, providerConsented, Address, Lat, Lon);

    private sealed class Deps
    {
        public FakeMatchRepository Matches { get; } = new();
        public FakeChatRepository Chats { get; } = new();
        public FakeWhatsapp Whatsapp { get; } = new();
        public TimeProvider Clock { get; } = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z"));
        public IOptions<ChatOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(
            new ChatOptions { PublicChatBaseUrl = "https://hook.test" });

        public MatchEntity SeedMatch()
        {
            var match = new MatchEntity
            {
                RequestId = Guid.NewGuid(),
                ProviderPhone = ProviderPhone,
                ServiceSlug = Slug
            };
            Matches.Stored[match.Id] = match;
            return match;
        }

        public ChatRoutingRequestedHandler Build()
        {
            var factory = new ChatSessionFactory(Chats, Options, Clock);
            return new ChatRoutingRequestedHandler(factory, Matches, Whatsapp,
                NullLogger<ChatRoutingRequestedHandler>.Instance);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeMatchRepository : IMatchRepository
    {
        public Dictionary<Guid, MatchEntity> Stored { get; } = new();

        public Task<MatchEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Stored.TryGetValue(id, out var m) ? m : null);
        public Task<IReadOnlyList<MatchEntity>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MatchEntity>>(Array.Empty<MatchEntity>());
        public Task AddAsync(MatchEntity match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<MatchEntity> matches, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryClaimPickAsync(PickClaim claim, CancellationToken ct = default) => Task.FromResult(true);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeChatRepository : IChatRepository
    {
        public List<ChatSession> Sessions { get; } = new();
        public List<ChatParticipant> Participants { get; } = new();

        public Task<ChatSession?> GetSessionAsync(Guid chatId, CancellationToken ct = default) =>
            Task.FromResult<ChatSession?>(null);
        public Task<ChatParticipant?> GetByTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<ChatParticipant?>(null);
        public Task<ChatParticipant?> GetParticipantAsync(Guid participantId, CancellationToken ct = default) =>
            Task.FromResult<ChatParticipant?>(null);
        public Task<ChatParticipant?> GetPeerAsync(Guid chatId, Guid exceptParticipantId, CancellationToken ct = default) =>
            Task.FromResult<ChatParticipant?>(null);
        public Task<IReadOnlyList<ChatParticipant>> GetParticipantsAsync(Guid chatId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatParticipant>>(Array.Empty<ChatParticipant>());
        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid chatId, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task AddSessionAsync(ChatSession session, CancellationToken ct = default)
        {
            Sessions.Add(session);
            return Task.CompletedTask;
        }
        public Task AddParticipantAsync(ChatParticipant participant, CancellationToken ct = default)
        {
            Participants.Add(participant);
            return Task.CompletedTask;
        }
        public Task AddParticipantsAsync(IEnumerable<ChatParticipant> participants, CancellationToken ct = default)
        {
            Participants.AddRange(participants);
            return Task.CompletedTask;
        }
        public Task<bool> TryAddMessageAsync(ChatMessage message, CancellationToken ct = default) => Task.FromResult(true);
        public Task AddAccessLogAsync(ChatAccessLog log, CancellationToken ct = default) => Task.CompletedTask;
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
}
