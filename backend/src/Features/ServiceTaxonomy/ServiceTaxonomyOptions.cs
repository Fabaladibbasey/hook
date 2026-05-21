using System.ComponentModel.DataAnnotations;

namespace Hook.Features.ServiceTaxonomy;

public class ServiceTaxonomyOptions
{
    public const string SectionName = "ServiceTaxonomy";

    [Range(0, 1)]
    public double AutoMergeThreshold { get; init; } = 0.85;

    [Range(0, 1)]
    public double AiJudgeThreshold { get; init; } = 0.50;

    [Range(1, 64)]
    public int MaxBatchSize { get; init; } = 16;

    // Advisory: documents the intended host-wide cap on concurrent isolated slug
    // resolutions. SlugResolver's actual gate is a static SemaphoreSlim sized at
    // type-init time from SlugResolver.DefaultMaxBatchParallelism; raising this
    // value alone does not re-tune the semaphore at runtime.
    [Range(1, 32)]
    public int MaxBatchParallelism { get; init; } = 4;
}
