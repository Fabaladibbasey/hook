using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp.Models;

public enum InboundMessageKind
{
    Unknown,
    Text,
    Location,
    Image,
    Audio,
    Document,
    Interactive,
    Reaction
}

public sealed record InboundMessage(
    string MessageId,
    PhoneNumber From,
    DateTimeOffset Timestamp,
    InboundMessageKind Kind,
    string? Text,
    InboundLocation? Location,
    string? RawJson);

public sealed record InboundLocation(double Latitude, double Longitude, string? Name, string? Address);
