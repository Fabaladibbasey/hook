namespace Hook.Features.ChatLifecycle.ProductiveSilence;

public sealed record ProductiveSilenceCheck(Guid ChatId, DateTimeOffset ScheduledForActivityAt);
