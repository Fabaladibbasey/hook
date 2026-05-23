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

    // After this elapses without a registration-resolution command callback,
    // RegistrationOrchestrator force-reverts a ResolvingServices draft so the user is
    // not trapped if the LLM dead-lettered or the host crashed mid-call. Keep aligned
    // with Wolverine.DefaultExecutionTimeout and OllamaOptions.TimeoutSeconds + 30 —
    // the three values move together.
    [Range(typeof(TimeSpan), "00:00:30", "00:30:00")]
    public TimeSpan ResolveStuckTtl { get; init; } = TimeSpan.FromSeconds(120);
}
