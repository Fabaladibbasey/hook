namespace Hook.Features.MetaTemplates;

public static class ProviderCheckInTemplate
{
    public const string Name = "provider_check_in";
    public const string Language = "en";

    public static object BuildPayload(string phoneE164, string servicesCsv) => new
    {
        messaging_product = "whatsapp",
        to = phoneE164.TrimStart('+'),
        type = "template",
        template = new
        {
            name = Name,
            language = new { code = Language },
            components = new[]
            {
                new
                {
                    type = "body",
                    parameters = new[]
                    {
                        new { type = "text", text = servicesCsv }
                    }
                }
            }
        }
    };
}
