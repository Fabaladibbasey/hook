using Hook.Shared.Domain;

namespace Hook.Features.ChatSession.AccessLog;

public class ChatAccessLog : IAggregateRoot
{
    public Guid Id { get; private init; }
    public Guid ChatId { get; private init; }
    public Guid ParticipantId { get; private init; }
    public DateTimeOffset OpenedAt { get; private init; }
    public string IpAddress { get; private init; } = string.Empty;
    public string DeviceInfo { get; private init; } = string.Empty;

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
