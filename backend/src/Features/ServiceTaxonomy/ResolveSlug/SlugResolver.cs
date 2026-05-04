using Hook.Features.Ai;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Microsoft.Extensions.Options;

namespace Hook.Features.ServiceTaxonomy.ResolveSlug;

public sealed class SlugResolver(
    IServiceRepository repository,
    IConversationAi ai,
    IOptions<ServiceTaxonomyOptions> options,
    ILogger<SlugResolver> logger)
{
    public async Task<ResolveSlugResult> ResolveAsync(
        string proposedSlug,
        string? rawExample,
        CancellationToken ct = default)
    {
        var normalized = Normalize(proposedSlug);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Proposed slug is empty after normalization.", nameof(proposedSlug));

        var existing = await repository.GetBySlugAsync(normalized, ct);
        if (existing is not null)
        {
            existing.RememberRawExample(rawExample ?? proposedSlug);
            await repository.SaveChangesAsync(ct);
            return new ResolveSlugResult(existing.Slug, SlugResolution.ReturnedExisting, 1.0);
        }

        var candidates = await repository.FindSimilarAsync(normalized, take: 3, ct);
        var top = candidates.FirstOrDefault();
        var topSim = top?.Similarity ?? 0;

        var opts = options.Value;

        if (top is not null && topSim >= opts.AutoMergeThreshold)
        {
            return await AcceptExistingAsync(top.Slug, rawExample ?? proposedSlug, SlugResolution.AutoMerged, topSim, ct);
        }

        if (top is not null && topSim >= opts.AiJudgeThreshold)
        {
            var candidateList = candidates.Select(c => c.Slug).ToArray();
            var judged = await ai.JudgeServiceMatchAsync(normalized, candidateList, ct);

            // Defense-in-depth: even though the IConversationAi adapter is supposed to
            // reject out-of-candidate matches at its own boundary, refuse here too. The
            // canonical Service taxonomy never accepts an LLM-invented slug — only one
            // already known to the database.
            var matched = judged.MatchedSlug;
            var isExistingMatch =
                !judged.IsNew
                && !string.IsNullOrEmpty(matched)
                && candidateList.Contains(matched, StringComparer.Ordinal);

            if (isExistingMatch)
            {
                return await AcceptExistingAsync(matched!, rawExample ?? proposedSlug, SlugResolution.AiJudgedMerge, topSim, ct);
            }
            logger.LogDebug("AI judged proposal {Slug} as new (top similarity {Sim:F2})", normalized, topSim);
        }

        var created = Service.Create(normalized, rawExample ?? proposedSlug);
        await repository.AddAsync(created, ct);
        await repository.SaveChangesAsync(ct);

        var resolution = top is not null && topSim >= opts.AiJudgeThreshold
            ? SlugResolution.AiJudgedNew
            : SlugResolution.Created;

        return new ResolveSlugResult(created.Slug, resolution, topSim);
    }

    private async Task<ResolveSlugResult> AcceptExistingAsync(string slug, string rawExample, SlugResolution resolution, double topSim, CancellationToken ct)
    {
        var existing = await repository.GetBySlugAsync(slug, ct);
        if (existing is null)
        {
            existing = Service.Create(slug, rawExample);
            await repository.AddAsync(existing, ct);
        }
        else
        {
            existing.RememberRawExample(rawExample);
        }
        await repository.SaveChangesAsync(ct);
        return new ResolveSlugResult(slug, resolution, topSim);
    }

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lower = raw.Trim().ToLowerInvariant();
        var replaced = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c)) replaced.Append(c);
            else if (c is ' ' or '-' or '_' or '/') replaced.Append('-');
        }
        var collapsed = System.Text.RegularExpressions.Regex.Replace(replaced.ToString(), "-{2,}", "-");
        return collapsed.Trim('-');
    }
}
