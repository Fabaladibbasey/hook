using System.ComponentModel.DataAnnotations;

namespace Hook.Features.ProviderAvailability;

public class ProviderAvailabilityOptions
{
    public const string SectionName = "ProviderAvailability";

    [Range(1, 168)]
    public int ExpiryHours { get; init; } = 24;

    [Range(1, 167)]
    public int RefreshPromptHours { get; init; } = 22;

    [Range(1, 20)]
    public int MaxServicesPerProvider { get; init; } = 5;
}
