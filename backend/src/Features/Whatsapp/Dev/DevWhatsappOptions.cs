namespace Hook.Features.Whatsapp.Dev;

public sealed class DevWhatsappOptions
{
    public const string SectionName = "Dev:Whatsapp";

    public bool Enabled { get; init; }

    public int OutboxRingSize { get; init; } = 200;
}
