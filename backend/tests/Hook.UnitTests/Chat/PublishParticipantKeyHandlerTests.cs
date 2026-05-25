using System.Security.Cryptography;
using Hook.Features.ChatSession;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.PublishKey;
using Hook.Shared.Pipeline.PostCommitSends;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class PublishParticipantKeyHandlerTests
{
    private readonly Mock<IChatRepository> _chats = new();
    private readonly Guid _chatId = Guid.NewGuid();
    private readonly ChatParticipant _self;
    private readonly ChatParticipant _peer;
    private readonly byte[] _validSpki;

    private bool _commitResult = true;

    public PublishParticipantKeyHandlerTests()
    {
        _self = ChatParticipant.Create(_chatId, ChatParticipantRole.Client, "+2203339999");
        _peer = ChatParticipant.Create(_chatId, ChatParticipantRole.Provider, "+2203331111");
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _validSpki = ecdh.ExportSubjectPublicKeyInfo();

        _chats.Setup(x => x.GetParticipantsAsync(_chatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { _self, _peer });
        _chats.Setup(x => x.TryCommitAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _commitResult);
    }

    private PublishParticipantKeyHandler Build() => new(_chats.Object);

    private PublishParticipantKeyCommand BuildCmd(byte[]? spki = null) =>
        new(SessionId: _self.CurrentSessionId,
            ChatId: _chatId,
            ParticipantId: _self.Id,
            PublicKeySpki: spki ?? _validSpki);

    [Fact]
    public async Task ParticipantMissing_ReturnsParticipantMissing()
    {
        _chats.Setup(x => x.GetParticipantsAsync(_chatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ChatParticipant>());

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.ParticipantMissing);
    }

    [Fact]
    public async Task WrongSession_ReturnsSessionRevoked()
    {
        var cmd = BuildCmd() with { SessionId = Guid.NewGuid() };

        var resp = await Build().Handle(cmd, CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.SessionRevoked);
        _self.PublicKey.ShouldBeNull();
    }

    [Fact]
    public async Task GarbageSpki_ReturnsInvalidKey()
    {
        // 32 random bytes is not a valid DER SPKI envelope — parse fails.
        var resp = await Build().Handle(BuildCmd(spki: new byte[32]), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.InvalidKey);
        _self.PublicKey.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptedWithPeer_ReturnsPeerKey()
    {
        // Peer already published; this side's publish returns the peer's key in the response.
        using var peerEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var peerSpki = peerEcdh.ExportSubjectPublicKeyInfo();
        _peer.SetPublicKey(peerSpki);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.Accepted);
        resp.Data.ShouldNotBeNull();
        resp.Data!.PeerParticipantId.ShouldBe(_peer.Id);
        resp.Data.PeerPublicKey.ShouldBe(peerSpki);
    }

    [Fact]
    public async Task AcceptedWithoutPeer_ReturnsEmptyPeerKey()
    {
        // Peer has not yet published — response carries an empty peer key array,
        // hub skips the caller-side PeerKeyAvailable send.
        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.Accepted);
        resp.Data.ShouldNotBeNull();
        resp.Data!.PeerPublicKey.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidP384Spki_ReturnsInvalidKey()
    {
        // Well-formed SPKI but wrong curve — peer's WebCrypto P-256 import would
        // reject, griefing the matched user off chat. Server must filter at publish
        // time. Pin the curve enforcement so a regression flipping ECDiffieHellman.Create()
        // back to platform-default trips CI.
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var p384Spki = ecdh.ExportSubjectPublicKeyInfo();

        var resp = await Build().Handle(BuildCmd(spki: p384Spki), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.InvalidKey);
        _self.PublicKey.ShouldBeNull();
    }

    [Fact]
    public async Task AcceptedWithPeer_RaisesBroadcastEvent()
    {
        // SetPublicKey raises BroadcastChatEvent on the aggregate; DomainEventScraper
        // ships it post-commit via the outbox. Pin the contract here.
        await Build().Handle(BuildCmd(), CancellationToken.None);

        var events = _self.DequeueEvents();
        var broadcast = events.OfType<BroadcastChatEvent>().Single();
        broadcast.ChatId.ShouldBe(_chatId);
        broadcast.EventName.ShouldBe(ChatHubEvents.PeerKeyAvailable);
        var payload = broadcast.Payload.ShouldBeOfType<PeerKeyAvailablePayload>();
        payload.PeerParticipantId.ShouldBe(_self.Id);
        payload.PeerPublicKeyB64.ShouldBe(Convert.ToBase64String(_validSpki));
    }

    [Fact]
    public async Task RotateBetweenCheckAndCommit_ReturnsSessionRevoked()
    {
        // TryCommitAsync returns false → simulates a concurrent RotateSession that
        // bumped ChatParticipant.Version between IsCurrentSession check and commit.
        _commitResult = false;

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(PublishParticipantKeyResult.SessionRevoked);
    }
}
