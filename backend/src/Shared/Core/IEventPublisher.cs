using Wolverine;

namespace Hook.Shared.Core;

/// <summary>
/// Thin abstraction over the Wolverine bus's publish path. Lets domain code
/// (PhoneExchanger, etc.) depend on a one-method interface instead of the full
/// IMessageBus surface, which keeps unit tests free of bus-mocking ceremony.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<T>(T message, CancellationToken ct = default);
}

// Adds scheduling on top of IEventPublisher. Services that originate scheduled
// messages (FeedbackResponseService) depend on this; pure publish consumers
// (PhoneExchanger) keep depending on the narrower IEventPublisher.
public interface IEventBus : IEventPublisher
{
    Task ScheduleAsync<T>(T message, TimeSpan delay, CancellationToken ct = default);
}

internal sealed class WolverineEventPublisher(IMessageBus bus) : IEventBus
{
    public async Task PublishAsync<T>(T message, CancellationToken ct = default) =>
        await bus.PublishAsync(message);

    // Wolverine's ScheduleAsync is an extension that hands off to PublishAsync
    // with a DeliveryOptions.ScheduleDelay — replicate that here so the wrapper
    // depends only on the IMessageBus interface members, never an extension.
    public async Task ScheduleAsync<T>(T message, TimeSpan delay, CancellationToken ct = default) =>
        await bus.PublishAsync(message, new DeliveryOptions { ScheduleDelay = delay });
}
