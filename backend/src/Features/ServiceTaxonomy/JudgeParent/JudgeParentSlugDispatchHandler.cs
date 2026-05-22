using Hook.Features.Ai;
using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class JudgeParentSlugDispatchHandler(
    IServiceRepository repository,
    IConversationAi ai,
    IMessageBus bus,
    IJudgeParentDedupGate dedup,
    ILogger<JudgeParentSlugDispatchHandler> logger)
{
    // [NonTransactional] avoids pinning an Npgsql connection across the 60-150s
    // Ollama window. bus.InvokeAsync is load-bearing — PublishAsync would skip
    // the durable outbox since this outer handler is unenrolled.
    [NonTransactional]
    public async Task Handle(JudgeParentSlugRequested evt, CancellationToken ct)
    {
        var svc = await repository.GetBySlugAsync(evt.Slug, ct);
        if (svc is null || !svc.IsRoot) return;
        if (RootSectorSeeder.RootSlugSet.Contains(evt.Slug)) return;

        if (!await dedup.TryClaimAsync(evt.Slug, ct))
        {
            logger.LogDebug("[Taxonomy] Dedup: skipping JudgeParent for {Slug}.", evt.Slug);
            return;
        }

        var parent = await ai.JudgeParentSlugAsync(evt.Slug, RootSectorSeeder.RootSlugs, svc.RawExamples, ct);
        if (parent is null) return;
        // Defense-in-depth: the adapter at OllamaConversationAi.cs:197 also
        // validates against `candidates`; both must use StringComparer.Ordinal.
        if (!RootSectorSeeder.RootSlugSet.Contains(parent))
        {
            logger.LogWarning("[Taxonomy] AI returned out-of-list parent {Parent} for {Slug}; dropping.", parent, evt.Slug);
            return;
        }
        if (string.Equals(parent, evt.Slug, StringComparison.Ordinal)) return;

        await bus.InvokeAsync(new AssignServiceParent(evt.Slug, parent), ct);
        logger.LogInformation("[Taxonomy] Assigned {Slug} -> {Parent}", evt.Slug, parent);
    }
}
