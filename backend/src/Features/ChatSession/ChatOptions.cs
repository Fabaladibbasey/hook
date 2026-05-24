using System.ComponentModel.DataAnnotations;

namespace Hook.Features.ChatSession;

public class ChatOptions
{
    public const string SectionName = "Chat";

    [Range(1, 1440)]
    public int IdleReminderMinutes { get; init; } = 20;

    [Range(1, 1440)]
    public int IdleEndMinutes { get; init; } = 30;

    [Range(1, 168)]
    public int HardExpiryHours { get; init; } = 24;

    // Productive-silence triggers Step1 feedback before auto-end when both sides
    // have exchanged enough messages and the chat then goes quiet. Lets us prompt
    // the client while the experience is still fresh instead of waiting for the
    // full idle-end window.
    [Range(1, 1440)]
    public int ProductiveSilenceMinutes { get; init; } = 10;

    [Range(1, 50)]
    public int ProductiveSilenceMinMessagesPerSide { get; init; } = 3;

    public string PublicChatBaseUrl { get; init; } = "http://localhost:5173";
}
