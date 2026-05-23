using Hook.Features.ChatLifecycle.Events;
using Hook.Features.ChatSession;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ChatLifecycle;

public sealed class ChatScheduler(IMessageBus bus, IOptions<ChatOptions> options)
{
    public async Task ScheduleIdleChecksAsync(
        Guid chatId,
        DateTimeOffset lastActivityAt,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        await bus.ScheduleAsync(
            new IdleReminderCheck(chatId, lastActivityAt),
            TimeSpan.FromMinutes(opts.IdleReminderMinutes));
        await bus.ScheduleAsync(
            new IdleEndCheck(chatId, lastActivityAt),
            TimeSpan.FromMinutes(opts.IdleEndMinutes));
    }

    public async Task ScheduleHardExpireAsync(Guid chatId, CancellationToken ct = default)
    {
        var opts = options.Value;
        await bus.ScheduleAsync(new HardExpireCheck(chatId), TimeSpan.FromHours(opts.HardExpiryHours));
    }
}
