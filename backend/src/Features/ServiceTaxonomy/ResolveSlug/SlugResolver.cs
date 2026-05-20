using Hook.Features.Ai;
using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.ServiceTaxonomy.ResolveSlug;

public class SlugResolver(
    IServiceRepository repository,
    IConversationAi ai,
    IMessageBus bus,
    IOptions<ServiceTaxonomyOptions> options,
    ILogger<SlugResolver> logger,
    IDbContextFactory<HookDbContext>? dbFactory = null)
{
    private const int MaxBatchParallelism = 4;

    public virtual async Task<IReadOnlyList<ResolveSlugResult>> ResolveBatchAsync(
        IReadOnlyList<string> proposedSlugs,
        string rawExample = "",
        CancellationToken ct = default)
    {
        if (proposedSlugs.Count == 0) return [];
        if (proposedSlugs.Count == 1 || dbFactory is null)
        {
            var sequential = new List<ResolveSlugResult>(proposedSlugs.Count);
            foreach (var slug in proposedSlugs)
                sequential.Add(await ResolveAsync(slug, rawExample, ct));
            return sequential;
        }

        using var gate = new SemaphoreSlim(MaxBatchParallelism, MaxBatchParallelism);
        var tasks = proposedSlugs.Select(async slug =>
        {
            await gate.WaitAsync(ct);
            try { return await ResolveIsolatedAsync(slug, rawExample, ct); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<ResolveSlugResult> ResolveIsolatedAsync(string proposedSlug, string rawExample, CancellationToken ct)
    {
        await using var db = await dbFactory!.CreateDbContextAsync(ct);
        var perCallRepo = new ServiceRepository(db);
        var perCallResolver = new SlugResolver(perCallRepo, ai, bus, options, logger);
        var result = await perCallResolver.ResolveAsync(proposedSlug, rawExample, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public virtual async Task<ResolveSlugResult> ResolveAsync(
        string proposedSlug,
        string rawExample = "",
        CancellationToken ct = default)
    {
        var normalized = Normalize(proposedSlug);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Proposed slug is empty after normalization.", nameof(proposedSlug));

        var memo = rawExample.Length > 0 ? rawExample : proposedSlug;
        var existing = await repository.GetBySlugAsync(normalized, ct);
        if (existing is not null)
        {
            existing.RememberRawExample(memo);
            return new ResolveSlugResult(existing.Slug, SlugResolution.ReturnedExisting, 1.0);
        }

        var candidates = await repository.FindSimilarAsync(normalized, take: 3, ct);
        var top = candidates.FirstOrDefault();
        var topSim = top?.Similarity ?? 0;

        var opts = options.Value;

        if (top is not null && topSim >= opts.AutoMergeThreshold)
        {
            return await AcceptExistingAsync(top.Slug, memo, SlugResolution.AutoMerged, topSim, ct);
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
                && matched.Length > 0
                && candidateList.Contains(matched, StringComparer.Ordinal);

            if (isExistingMatch)
            {
                return await AcceptExistingAsync(matched, memo, SlugResolution.AiJudgedMerge, topSim, ct);
            }
            logger.LogDebug("AI judged proposal {Slug} as new (top similarity {Sim:F2})", normalized, topSim);
        }

        var created = Service.Create(normalized, memo);
        await repository.AddAsync(created, ct);
        await PublishParentJudgmentAsync(normalized);

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
            await PublishParentJudgmentAsync(slug);
        }
        else
        {
            existing.RememberRawExample(rawExample);
        }
        return new ResolveSlugResult(slug, resolution, topSim);
    }

    // Post-commit parent inference — handler reads RawExamples + runs AI without
    // blocking the inbound funnel. Callers must invoke from inside a Wolverine
    // handler context (AutoApplyTransactions + outbox) so the envelope is
    // durable; see NonHandlerContextEventLossTests for the parallel guard pattern.
    private ValueTask PublishParentJudgmentAsync(string slug) =>
        bus.PublishAsync(new JudgeParentSlugRequested(slug));

    // ASCII-only kebab-case: rejects Unicode / Cyrillic / CJK homoglyphs that
    // could otherwise survive ToLowerInvariant + IsLetterOrDigit and poison
    // downstream slug-keyed maps (LLM prompts, jsonb membership predicates).
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lower = raw.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastWasDash = false;
        foreach (var c in lower)
        {
            char emit;
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) emit = c;
            else if (c is ' ' or '-' or '_' or '/') emit = '-';
            else continue;

            if (emit == '-' && lastWasDash) continue;
            sb.Append(emit);
            lastWasDash = emit == '-';
        }
        return sb.ToString().Trim('-');
    }
}
