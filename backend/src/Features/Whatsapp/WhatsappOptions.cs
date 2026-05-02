using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Whatsapp;

public class WhatsappOptions
{
    public const string SectionName = "Whatsapp";

    [Required]
    public string VerifyToken { get; init; } = string.Empty;

    [Required]
    public string AppSecret { get; init; } = string.Empty;

    [Required]
    public string PhoneNumberId { get; init; } = string.Empty;

    [Required]
    public string AccessToken { get; init; } = string.Empty;

    public string GraphApiVersion { get; init; } = "v22.0";

    public string GraphApiBaseUrl { get; init; } = "https://graph.facebook.com";
}
