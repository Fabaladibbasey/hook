using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook.TestHelpers;

public static class OutboxDrain
{
    // Polls wolverine.wolverine_outgoing_envelopes until every envelope has been
    // dispatched (Wolverine deletes rows on success). Use between "publish inbound"
    // and "assert reply text" so the durable outbox catches up with the test
    // assertions before they run.
    public static async Task WaitForOutboxDrainAsync(
        IServiceProvider services,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        var poll = TimeSpan.FromMilliseconds(100);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        while (true)
        {
            var count = await db.Database
                .SqlQueryRaw<long>("SELECT COUNT(*)::bigint AS \"Value\" FROM wolverine.wolverine_outgoing_envelopes")
                .SingleAsync(ct);
            if (count == 0) return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Wolverine outbox did not drain within {effectiveTimeout}; {count} envelope(s) remaining.");
            await Task.Delay(poll, ct);
        }
    }
}
