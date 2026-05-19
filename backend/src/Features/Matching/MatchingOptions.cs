using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Matching;

public class MatchingOptions
{
    public const string SectionName = "Matching";

    [Range(0.5, 100)]
    public double DefaultRadiusKm { get; init; } = 5;

    [Range(1, 1000)]
    public double MaxRadiusKm { get; init; } = 100;

    [Range(1, 10)]
    public double RadiusExpansionFactor { get; init; } = 2;

    [Range(0, 1)]
    public double DistanceWeight { get; init; } = 0.6;

    [Range(0, 1)]
    public double RecencyWeight { get; init; } = 0.3;

    [Range(0, 1)]
    public double SuccessWeight { get; init; } = 0.1;

    [Range(0.1, 168)]
    public double RecencyHalfLifeHours { get; init; } = 12;

    [Range(0, 100)]
    public int ColdStartMinJobs { get; init; } = 3;

    [Range(1, 10)]
    public int TopMatchesPerBatch { get; init; } = 3;

    // Broadened = provider matched a parent of the requested slug ("doctor" for
    // "cardiology"). More abuse-prone (a generalist could blanket every child
    // specialist's requests), so a harsher discount is applied.
    [Range(0.1, 1.0)]
    public double BroadenedMatchFactor { get; init; } = 0.4;

    // Narrowed = provider matched a child of the requested slug ("cardiology"
    // for "doctor"). Legitimate niche routing, so the discount is modest.
    [Range(0.1, 1.0)]
    public double NarrowedMatchFactor { get; init; } = 0.6;
}
