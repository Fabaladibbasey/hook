using System.ComponentModel.DataAnnotations;

namespace Hook.Features.ServiceTaxonomy;

public class ServiceTaxonomyOptions
{
    public const string SectionName = "ServiceTaxonomy";

    [Range(0, 1)]
    public double AutoMergeThreshold { get; init; } = 0.85;

    [Range(0, 1)]
    public double AiJudgeThreshold { get; init; } = 0.50;
}
