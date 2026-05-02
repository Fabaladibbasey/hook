namespace Hook.Features.MetaTemplates;

public class WhatsappContact
{
    public required string Phone { get; init; }
    public DateTimeOffset LastInboundAt { get; set; }
}
