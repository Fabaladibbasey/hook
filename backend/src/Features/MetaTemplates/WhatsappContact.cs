using Hook.Shared.Domain;

namespace Hook.Features.MetaTemplates;

public class WhatsappContact : IAggregateRoot
{
    public string Phone { get; private init; } = string.Empty;
    public DateTimeOffset LastInboundAt { get; private set; }

    // Tip throttle. Non-nullable to honour the no-nullable-primitives rule.
    // Two sentinel-values exist by design: CLR new-instance default (0001-01-01)
    // for in-memory rows we never persisted, and the DB column default
    // ('1970-01-01') for rows that survive an EF round-trip. Both compare less
    // than any real cooldown horizon, so the time-only cooldown math tolerates both.
    public string LastTipKey { get; private set; } = string.Empty;
    public DateTimeOffset LastTipAt { get; private set; }

    public static WhatsappContact Recorded(string phone, DateTimeOffset at) => new()
    {
        Phone = phone,
        LastInboundAt = at
    };
}
