using Hook.Features.Ai;
using Hook.Features.ServiceTaxonomy.SeedRoots;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class JudgeParentSlugDispatchHandler(
    IServiceRepository repository,
    IConversationAi ai,
    IJudgeParentDedupGate dedup,
    ILogger<JudgeParentSlugDispatchHandler> logger)
{
    // [NonTransactional] avoids pinning an Npgsql connection across the 60-150s
    // Ollama window. bus.PublishAsync enqueues AssignServiceParentCommand to the
    // durable outbox so the apply-handler commits without blocking the AI worker.
    [NonTransactional]
    public async Task Handle(JudgeParentSlugCommand cmd, IMessageBus bus, CancellationToken ct)
    {
        var svc = await repository.GetBySlugAsync(cmd.Slug, ct);
        if (svc is null || !svc.IsRoot) return;
        if (RootSectorSeeder.RootSlugSet.Contains(cmd.Slug)) return;

        if (!await dedup.TryClaimAsync(cmd.Slug, ct))
        {
            logger.LogDebug("[Taxonomy] Dedup: skipping JudgeParent for {Slug}.", cmd.Slug);
            return;
        }

        var parent = await ai.JudgeParentSlugAsync(cmd.Slug, RootSectorSeeder.RootSlugs, svc.RawExamples, ct);
        if (parent is null) return;
        // Defense-in-depth: the adapter at OllamaConversationAi.cs:197 also
        // validates against `candidates`; both must use StringComparer.Ordinal.
        if (!RootSectorSeeder.RootSlugSet.Contains(parent))
        {
            logger.LogWarning(
                "[Taxonomy] AI returned out-of-list parent {Parent} for {Slug}; dropping.",
                parent,
                cmd.Slug);
            return;
        }
        if (string.Equals(parent, cmd.Slug, StringComparison.Ordinal)) return;

        await bus.PublishAsync(new AssignServiceParentCommand(cmd.Slug, parent));
        logger.LogInformation("[Taxonomy] Assigned {Slug} -> {Parent}", cmd.Slug, parent);
    }
}
