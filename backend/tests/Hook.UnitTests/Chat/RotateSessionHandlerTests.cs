using Hook.Features.ChatSession;
using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.OpenChat;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class RotateSessionHandlerTests
{
    private readonly Mock<IChatRepository> _chats = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly ChatSession _session;
    private readonly ChatParticipant _participant;
    private const string Token = "rot-token";

    public RotateSessionHandlerTests()
    {
        _session = ChatSession.Create(TimeSpan.FromMinutes(30), _clock.GetUtcNow());
        _participant = ChatParticipant.Create(_session.Id, ChatParticipantRole.Client, "+2203339999");

        _chats.Setup(x => x.GetByTokenAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_participant);
        _chats.Setup(x => x.GetSessionAsync(_session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_session);
    }

    private RotateSessionHandler Build() => new(_chats.Object, _clock);
    private RotateSessionCommand BuildCmd() => new(Token, "1.2.3.4", "ua/1");

    [Fact]
    public async Task InvalidToken_ReturnsNotFound_WithNullData()
    {
        _chats.Setup(x => x.GetByTokenAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatParticipant?)null);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(RotateSessionResult.NotFound);
        resp.Data.ShouldBeNull();
    }

    [Fact]
    public async Task MissingSession_ReturnsNotFound_WithNullData()
    {
        _chats.Setup(x => x.GetSessionAsync(_session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatSession?)null);

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(RotateSessionResult.NotFound);
        resp.Data.ShouldBeNull();
    }

    [Fact]
    public async Task Rotated_ReturnsDataWithNewSessionId_AndBumpsParticipantVersion()
    {
        var priorSessionId = _participant.CurrentSessionId;
        var priorVersion = _participant.Version;

        var resp = await Build().Handle(BuildCmd(), CancellationToken.None);

        resp.Result.ShouldBe(RotateSessionResult.Rotated);
        resp.Data.ShouldNotBeNull();
        resp.Data!.SessionId.ShouldNotBe(priorSessionId);
        resp.Data.SessionId.ShouldBe(_participant.CurrentSessionId);
        resp.Data.ChatId.ShouldBe(_participant.ChatId);
        resp.Data.ParticipantId.ShouldBe(_participant.Id);
        resp.Data.Role.ShouldBe(_participant.Role.ToString());
        resp.Data.Status.ShouldBe(_session.Status.ToString());
        resp.Data.ExpiresAt.ShouldBe(_session.ExpiresAt);
        _participant.Version.ShouldBeGreaterThan(priorVersion);
    }

    [Fact]
    public async Task Rotated_PersistsAccessLog_WithIpAndUa()
    {
        ChatAccessLog? captured = null;
        _chats.Setup(x => x.AddAccessLogAsync(It.IsAny<ChatAccessLog>(), It.IsAny<CancellationToken>()))
            .Callback<ChatAccessLog, CancellationToken>((log, _) => captured = log)
            .Returns(Task.CompletedTask);

        await Build().Handle(BuildCmd(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.ChatId.ShouldBe(_participant.ChatId);
        captured.ParticipantId.ShouldBe(_participant.Id);
        captured.IpAddress.ShouldBe("1.2.3.4");
        captured.DeviceInfo.ShouldBe("ua/1");
    }

    [Fact]
    public async Task Rotated_PriorSessionId_NoLongerCurrent()
    {
        var priorSessionId = _participant.CurrentSessionId;

        await Build().Handle(BuildCmd(), CancellationToken.None);

        _participant.IsCurrentSession(priorSessionId).ShouldBeFalse();
    }
}
