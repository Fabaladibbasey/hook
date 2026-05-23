using System.Text.Json;
using System.Text.Json.Serialization;
using Hook.Features.ChatSession;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatMessageRejectedTests
{
    // Mirrors the SignalR JSON protocol configured in ChatServiceCollectionExtensions:
    // camelCase property keys + JsonStringEnumConverter so the wire enum is a string,
    // matching the `reason: string` contract in frontend/src/features/chat/useChatHub.ts:23.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ChatMessageRejected_SerializesToCamelCaseKeys_WithExpectedValueTypes()
    {
        var dto = new ChatMessageRejected(Guid.NewGuid(), ChatMessageRejectReason.DecodeFailed);

        var json = JsonSerializer.Serialize(dto, WireOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("messageId").GetGuid().ShouldBe(dto.MessageId);
        root.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.String);
        root.GetProperty("reason").GetString().ShouldBe("DecodeFailed");
    }

    [Theory]
    [InlineData(ChatMessageRejectReason.InvalidPayload, "InvalidPayload")]
    [InlineData(ChatMessageRejectReason.Replay, "Replay")]
    [InlineData(ChatMessageRejectReason.DecodeFailed, "DecodeFailed")]
    [InlineData(ChatMessageRejectReason.SessionEnded, "SessionEnded")]
    [InlineData(ChatMessageRejectReason.Duplicate, "Duplicate")]
    public void ChatMessageRejected_ReasonSerializesAsEnumName(ChatMessageRejectReason reason, string expected)
    {
        var dto = new ChatMessageRejected(Guid.NewGuid(), reason);

        var json = JsonSerializer.Serialize(dto, WireOptions);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("reason").GetString().ShouldBe(expected);
    }
}
