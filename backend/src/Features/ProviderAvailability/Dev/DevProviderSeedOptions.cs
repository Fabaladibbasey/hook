using System.ComponentModel.DataAnnotations;

namespace Hook.Features.ProviderAvailability.Dev;

public sealed class DevProviderSeedOptions
{
    public const string SectionName = "Dev:Providers";

    public bool Enabled { get; init; }

    public bool AutoSeed { get; init; }

    [Range(-90, 90)]
    public double ReferenceLat { get; init; } = 13.4549;

    [Range(-180, 180)]
    public double ReferenceLng { get; init; } = -16.5790;

    [Range(1, 168)]
    public int TtlHours { get; init; } = 24;

    public IReadOnlyList<string> Phones { get; init; } = Array.Empty<string>();
}
