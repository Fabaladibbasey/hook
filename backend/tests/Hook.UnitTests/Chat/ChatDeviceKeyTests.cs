using Hook.Features.ChatSession.ParticipantAggregate;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatDeviceKeyTests
{
    private static ChatDeviceKey NewKey() => new()
    {
        ChatId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        PublicKey = [0x01]
    };

    [Fact]
    public void TryAdvanceSequence_accepts_strictly_greater_value()
    {
        var key = NewKey();
        key.TryAdvanceSequence(10).ShouldBeTrue();
        key.LastInboundSequence.ShouldBe(10);
    }

    [Fact]
    public void TryAdvanceSequence_rejects_equal_value()
    {
        var key = NewKey();
        key.TryAdvanceSequence(10).ShouldBeTrue();
        key.TryAdvanceSequence(10).ShouldBeFalse();
        key.LastInboundSequence.ShouldBe(10);
    }

    [Fact]
    public void TryAdvanceSequence_rejects_lower_value()
    {
        var key = NewKey();
        key.TryAdvanceSequence(20).ShouldBeTrue();
        key.TryAdvanceSequence(15).ShouldBeFalse();
        key.LastInboundSequence.ShouldBe(20);
    }

    [Fact]
    public void Two_devices_for_same_participant_have_independent_sequences()
    {
        var participantId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var deviceA = new ChatDeviceKey
        {
            ChatId = chatId,
            ParticipantId = participantId,
            DeviceId = Guid.NewGuid(),
            PublicKey = [0xAA]
        };
        var deviceB = new ChatDeviceKey
        {
            ChatId = chatId,
            ParticipantId = participantId,
            DeviceId = Guid.NewGuid(),
            PublicKey = [0xBB]
        };

        deviceA.TryAdvanceSequence(100).ShouldBeTrue();
        deviceB.TryAdvanceSequence(50).ShouldBeTrue();

        deviceA.LastInboundSequence.ShouldBe(100);
        deviceB.LastInboundSequence.ShouldBe(50);
    }
}
