using System.Text.Json;
using Hook.Features.ChatSession.OpenChat;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class OpenChatResponseTests
{
    [Fact]
    public void OpenChatResponse_SerializesToCamelCaseKeys()
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

        root.TryGetProperty("chatId", out _).ShouldBeTrue();
        root.TryGetProperty("participantId", out _).ShouldBeTrue();
        root.TryGetProperty("role", out _).ShouldBeTrue();
        root.TryGetProperty("sessionId", out _).ShouldBeTrue();
        root.TryGetProperty("status", out _).ShouldBeTrue();
        root.TryGetProperty("expiresAt", out _).ShouldBeTrue();
    }
}
