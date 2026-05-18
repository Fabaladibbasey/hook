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

    public string PublicChatBaseUrl { get; init; } = "http://localhost:5173";
}
