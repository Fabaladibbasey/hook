using System.Collections.Frozen;
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
    TimeProvider clock,
    ILogger<SlugResolver> logger,
    IDbContextFactory<HookDbContext>? dbFactory = null)
{
    // Static cap (host-wide, prevents per-call instances multiplying concurrency by caller
    // count). Sized from this default at type-init time. To raise it in production, change
    // the default AND restart — the gate is not hot-reloadable. ServiceTaxonomyOptions.
    // MaxBatchParallelism documents this bound but is advisory only.
    public const int DefaultMaxBatchParallelism = 4;
    private static readonly SemaphoreSlim BatchGate =
        new(DefaultMaxBatchParallelism, DefaultMaxBatchParallelism);

    public virtual async Task<IReadOnlyList<ResolveSlugResult>> ResolveBatchAsync(
        IReadOnlyList<string> proposedSlugs,
        string rawExample = "",
        CancellationToken ct = default)
    {
        if (proposedSlugs.Count == 0) return [];

        var opts = options.Value;
        if (proposedSlugs.Count > opts.MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch size {proposedSlugs.Count} exceeds MaxBatchSize {opts.MaxBatchSize}.",
                nameof(proposedSlugs));
        }

        // Normalize once + dedupe by normalized value. Two raws that collapse to the same
        // slug (e.g. "Plumbing" + "plumbing-") would otherwise race the PK insert.
        var normalizedToRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in proposedSlugs)
        {
            var n = Normalize(raw);
            if (n.Length == 0) continue;
            normalizedToRaw.TryAdd(n, raw);
        }
        if (normalizedToRaw.Count == 0) return [];

        // Sequential single-slug or no-factory path. Reuse the public ResolveAsync so the
        // post-commit publish happens on the outer ambient context (handler tx + outbox).
        if (normalizedToRaw.Count == 1 || dbFactory is null)
        {
            var results = new List<ResolveSlugResult>(normalizedToRaw.Count);
            foreach (var (normalized, raw) in normalizedToRaw)
                results.Add(await ResolveAsync(normalized, rawExample.Length > 0 ? rawExample : raw, ct));
            return results;
        }

        // Inner contexts commit on their own connections; under Postgres default isolation
        // (READ COMMITTED) a sibling's FindSimilarAsync would otherwise see the first
        // committer's row and auto-merge, silently dropping the loser (timing-dependent
        // which name wins). The per-call peer set (self pre-removed) makes resolution
        // deterministic regardless of commit order. Cross-batch auto-merge (a later batch
        // finding a prior committed row) still works via GetBySlugAsync short-circuit.
        var allPeers = normalizedToRaw.Keys.ToFrozenSet(StringComparer.Ordinal);

        var tasks = normalizedToRaw.Select(async kvp =>
        {
            await BatchGate.WaitAsync(ct);
            try
            {
                var peersExcludingSelf = allPeers.Count == 1
                    ? FrozenSet<string>.Empty
                    : allPeers.Where(p => p != kvp.Key).ToFrozenSet(StringComparer.Ordinal);
                return await ResolveIsolatedAsync(kvp.Key, kvp.Value, rawExample, peersExcludingSelf, ct);
            }
            finally { BatchGate.Release(); }
        });
        var pairs = await Task.WhenAll(tasks);

        // Issue publishes on the OUTER ambient (handler) context so envelopes enrol
        // in the durable outbox alongside the caller's tx. Sequential by design: outbox
        // writes share the outer EF tx's single Npgsql connection; parallel publishes
        // would conflict on the connection.
        foreach (var (_, publishSlug) in pairs)
        {
            if (publishSlug is null) continue;
            await PublishParentJudgmentAsync(publishSlug);
        }

        return Array.ConvertAll(pairs, p => p.Result);
    }

    private async Task<(ResolveSlugResult Result, string? PublishSlug)> ResolveIsolatedAsync(
        string normalizedSlug, string rawSlug, string rawExample,
        IReadOnlySet<string> peersExcludingSelf, CancellationToken ct)
    {
        await using var db = await dbFactory!.CreateDbContextAsync(ct);
        var perCallRepo = new ServiceRepository(db);
        var perCallResolver = new SlugResolver(perCallRepo, ai, bus, options, clock, logger);
        var memo = rawExample.Length > 0 ? rawExample : rawSlug;
        var pair = await perCallResolver.ResolveCoreAsync(normalizedSlug, memo, peersExcludingSelf, ct);
        await db.SaveChangesAsync(ct);
        return pair;
    }

    public virtual async Task<ResolveSlugResult> ResolveAsync(
        string proposedSlug,
        string rawExample = "",
        CancellationToken ct = default)
    {
        var (result, publishSlug) = await ResolveCoreAsync(
            proposedSlug, rawExample, FrozenSet<string>.Empty, ct);
        if (publishSlug is not null)
            await PublishParentJudgmentAsync(publishSlug);
        return result;
    }

    // Returns PublishSlug != null when the caller must publish JudgeParentSlugCommand
    // for it. Splitting publish from resolution lets the batch path defer publishes to
    // the outer ambient context after inner isolated contexts commit.
    //
    // batchPeers excludes intra-batch sibling slugs (self pre-removed by the caller) so
    // a sibling row inserted on a separate connection cannot dominate FindSimilarAsync
    // and silently absorb the loser. Cross-batch auto-merge still fires via the
    // GetBySlugAsync short-circuit above. Pass FrozenSet<string>.Empty on the
    // single-call path.
    internal async Task<(ResolveSlugResult Result, string? PublishSlug)> ResolveCoreAsync(
        string proposedSlug,
        string rawExample,
        IReadOnlySet<string> batchPeers,
        CancellationToken ct)
    {
        var normalized = Normalize(proposedSlug);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Proposed slug is empty after normalization.", nameof(proposedSlug));

        var memo = rawExample.Length > 0 ? rawExample : proposedSlug;
        var existing = await repository.GetBySlugAsync(normalized, ct);
        if (existing is not null)
        {
            existing.RememberRawExample(memo);
            return (new ResolveSlugResult(existing.Slug, SlugResolution.ReturnedExisting, 1.0), null);
        }

        var candidates = await repository.FindSimilarAsync(normalized, take: 3, ct);
        if (batchPeers.Count > 0 && candidates.Count > 0)
        {
            // Guard the rebuild — only allocate a new array when a peer actually shows
            // up in the candidate list. Bounded by take=3, but avoid the allocation on
            // the common (no-collision) path. Self never appears here because the
            // caller pre-removes the current slug from the peer set.
            var hasPeer = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (batchPeers.Contains(candidates[i].Slug)) { hasPeer = true; break; }
            }
            if (hasPeer)
                candidates = candidates.Where(c => !batchPeers.Contains(c.Slug)).ToArray();
        }
        var top = candidates.FirstOrDefault();
        var topSim = top?.Similarity ?? 0;

        var opts = options.Value;

        if (top is not null && topSim >= opts.AutoMergeThreshold)
        {
            return await AcceptExistingCoreAsync(top.Slug, memo, SlugResolution.AutoMerged, topSim, ct);
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
                return await AcceptExistingCoreAsync(matched, memo, SlugResolution.AiJudgedMerge, topSim, ct);
            }
            logger.LogDebug("AI judged proposal {Slug} as new (top similarity {Sim:F2})", normalized, topSim);
        }

        var created = Service.Create(normalized, clock.GetUtcNow(), memo);
        await repository.AddAsync(created, ct);

        var resolution = top is not null && topSim >= opts.AiJudgeThreshold
            ? SlugResolution.AiJudgedNew
            : SlugResolution.Created;

        return (new ResolveSlugResult(created.Slug, resolution, topSim), normalized);
    }

    private async Task<(ResolveSlugResult Result, string? PublishSlug)> AcceptExistingCoreAsync(
        string slug, string rawExample, SlugResolution resolution, double topSim, CancellationToken ct)
    {
        var existing = await repository.GetBySlugAsync(slug, ct);
        if (existing is null)
        {
            existing = Service.Create(slug, clock.GetUtcNow(), rawExample);
            await repository.AddAsync(existing, ct);
            return (new ResolveSlugResult(slug, resolution, topSim), slug);
        }
        existing.RememberRawExample(rawExample);
        return (new ResolveSlugResult(slug, resolution, topSim), null);
    }

    // Post-commit parent inference — handler reads RawExamples + runs AI without
    // blocking the inbound funnel. Callers must invoke from inside a Wolverine
    // handler context (AutoApplyTransactions + outbox) so the envelope is
    // durable; see NonHandlerContextEventLossTests for the parallel guard pattern.
    private ValueTask PublishParentJudgmentAsync(string slug) =>
        bus.PublishAsync(new JudgeParentSlugCommand(slug));

    // Max stored slug length. Service.Slug PK has HasMaxLength(80); cap below that so
    // trim slack remains for future suffixing (e.g. disambiguation).
    private const int MaxSlugLength = 64;

    // ASCII-only kebab-case: rejects Unicode / Cyrillic / CJK homoglyphs that
    // could otherwise survive ToLowerInvariant + IsLetterOrDigit and poison
    // downstream slug-keyed maps (LLM prompts, jsonb membership predicates).
    // Truncates to MaxSlugLength so a misbehaving caller cannot push a 1KB string
    // through to the database.
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lower = raw.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(Math.Min(lower.Length, MaxSlugLength));
        var lastWasDash = false;
        foreach (var c in lower)
        {
            if (sb.Length >= MaxSlugLength) break;

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
