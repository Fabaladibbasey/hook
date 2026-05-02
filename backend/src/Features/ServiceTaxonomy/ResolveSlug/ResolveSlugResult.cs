namespace Hook.Features.ServiceTaxonomy.ResolveSlug;

public enum SlugResolution
{
    AutoMerged,
    AiJudgedMerge,
    AiJudgedNew,
    Created,
    ReturnedExisting
}

public sealed record ResolveSlugResult(string CanonicalSlug, SlugResolution Resolution, double TopSimilarity);
