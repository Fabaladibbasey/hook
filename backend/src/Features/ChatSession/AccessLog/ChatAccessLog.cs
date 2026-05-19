using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.AccessLog;

public class ChatAccessLog : IAggregateRoot
{
    public Guid Id { get; init; }
    public required Guid ChatId { get; init; }
    public required Guid ParticipantId { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string DeviceInfo { get; init; } = string.Empty;

    public static ChatAccessLog Record(
        Guid chatId,
        Guid participantId,
        string ipAddress,
        string deviceInfo,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            ChatId = chatId,
            ParticipantId = participantId,
            IpAddress = ipAddress,
            DeviceInfo = deviceInfo,
            OpenedAt = now
        };
}
