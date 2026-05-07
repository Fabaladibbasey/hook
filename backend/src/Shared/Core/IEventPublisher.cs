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

internal sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public async Task PublishAsync<T>(T message, CancellationToken ct = default) =>
        await bus.PublishAsync(message);
}
