using System.Text.Json;
using Hook.Features.ChatSession;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class EncryptedChatMessageTests
{
    // Pins the SignalR wire shape consumed by frontend/src/features/chat/useChatHub.ts:13
    // (WireMessage). Camel-cased keys, typed values per field.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void EncryptedChatMessage_SerializesToCamelCaseKeys_WithExpectedValueTypes()
    {
        var msg = new EncryptedChatMessage(
            MessageId: Guid.NewGuid(),
            CiphertextB64: "Y2lwaGVydGV4dA==",
            NonceB64: "bm9uY2U=",
            Sequence: 42);

        var json = JsonSerializer.Serialize(msg, WireOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("messageId").GetGuid().ShouldBe(msg.MessageId);
        root.GetProperty("ciphertextB64").GetString().ShouldBe(msg.CiphertextB64);
        root.GetProperty("nonceB64").GetString().ShouldBe(msg.NonceB64);
        root.GetProperty("sequence").GetInt64().ShouldBe(msg.Sequence);
    }

    [Fact]
    public void EncryptedChatMessage_RoundTrips_PreservesAllFields()
    {
        var original = new EncryptedChatMessage(
            MessageId: Guid.NewGuid(),
            CiphertextB64: "Y2lwaGVydGV4dA==",
            NonceB64: "bm9uY2U=",
            Sequence: long.MaxValue);

        var json = JsonSerializer.Serialize(original, WireOptions);
        var roundTripped = JsonSerializer.Deserialize<EncryptedChatMessage>(json, WireOptions);

        roundTripped.ShouldBe(original);
    }
}
