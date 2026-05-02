using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ProviderAvailability.Refresh;

public sealed class ProviderRefreshScheduler(IMessageBus bus, IOptions<ProviderAvailabilityOptions> options)
{
    public async Task ScheduleAsync(string phone, DateTimeOffset lastActiveAt, CancellationToken ct = default)
    {
        var delay = TimeSpan.FromHours(options.Value.RefreshPromptHours);
        await bus.ScheduleAsync(new ProviderRefreshCheck(phone, lastActiveAt), delay);
    }
}
