namespace Hook.Features.ChatLifecycle.Events;

public sealed record IdleReminderCheck(Guid ChatId, DateTimeOffset LastActivityAt);
public sealed record IdleEndCheck(Guid ChatId, DateTimeOffset LastActivityAt);
public sealed record HardExpireCheck(Guid ChatId);
