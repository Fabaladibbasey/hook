using System.ComponentModel.DataAnnotations;

namespace Hook.Features.RateLimiting;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    [Range(1, 100)]
    public int BurstTokens { get; init; } = 3;

    [Range(1, 600)]
    public int BurstWindowSeconds { get; init; } = 5;

    [Range(1, 1000)]
    public int SpamPerHour { get; init; } = 30;

    [Range(1, 1000)]
    public int AvailabilityPerDay { get; init; } = 10;

    [Range(1, 1000)]
    public int RequestsPerDay { get; init; } = 20;
}
