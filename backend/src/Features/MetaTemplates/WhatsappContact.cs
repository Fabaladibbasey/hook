using Hook.Shared.Domain;

namespace Hook.Features.MetaTemplates;

public class WhatsappContact : IAggregateRoot
{
    public string Phone { get; private init; } = string.Empty;
    public DateTimeOffset LastInboundAt { get; private set; }

    public static WhatsappContact Recorded(string phone, DateTimeOffset at) => new()
    {
        Phone = phone,
        LastInboundAt = at
    };
}
