using System.Text.Json;
using Hook.Features.ChatSession.OpenChat;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class OpenChatResponseTests
{
    [Fact]
    public void OpenChatResponse_SerializesToCamelCaseKeys_WithExpectedValueTypes()
    {
        var response = new OpenChatResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Client",
            Guid.NewGuid(),
            "Active",
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("chatId").GetGuid().ShouldBe(response.ChatId);
        root.GetProperty("participantId").GetGuid().ShouldBe(response.ParticipantId);
        root.GetProperty("role").GetString().ShouldBe(response.Role);
        root.GetProperty("sessionId").GetGuid().ShouldBe(response.SessionId);
        root.GetProperty("status").GetString().ShouldBe(response.Status);
        root.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(response.ExpiresAt);
    }
}
