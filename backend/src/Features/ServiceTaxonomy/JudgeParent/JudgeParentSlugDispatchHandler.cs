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
    ILogger<JudgeParentSlugDispatchHandler> logger)
{
    /// <summary>
    /// Bridges anonymous WhatsApp inbound -> Ollama parent inference -> durable
    /// aggregate mutation.
    /// </summary>
    /// <remarks>
    /// Outer is <see cref="NonTransactionalAttribute"/> so AutoApplyTransactions
    /// does not pin an Npgsql connection across the 60-150s Ollama window. The
    /// inner <c>AssignServiceParent</c> handler is default-transactional and
    /// idempotent (no-ops on non-root), so re-firing this envelope after a
    /// transient AI / network failure is safe. <c>bus.InvokeAsync</c> is load-
    /// bearing -- switching to <c>PublishAsync</c> would break the exactly-once
    /// guarantee since this outer handler is not enrolled in the durable outbox.
    /// </remarks>
    [NonTransactional]
    public async Task Handle(JudgeParentSlugRequested evt, CancellationToken ct)
    {
        var svc = await repository.GetBySlugAsync(evt.Slug, ct);
        if (svc is null || !svc.IsRoot) return;

        // RootSectorSeeder.RootSlugs is append-only per the seeder contract, so
        // reading the static list avoids an extra DB roundtrip per inference.
        var roots = RootSectorSeeder.RootSlugs;
        // Defense-in-depth: SlugResolver normally won't republish for a seeded
        // sector (it short-circuits on GetBySlugAsync hit), but if the row was
        // deleted (retention sweep, manual cleanup) and re-resolved we'd
        // otherwise ask AI to parent a sector that owns its own subtree.
        if (roots.Contains(evt.Slug, StringComparer.Ordinal)) return;

        string? parent;
        try
        {
            parent = await ai.JudgeParentSlugAsync(evt.Slug, roots, svc.RawExamples, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[Taxonomy] Parent inference failed for {Slug}; staying root.", evt.Slug);
            return;
        }

        if (parent is null) return;
        if (!roots.Contains(parent, StringComparer.Ordinal))
        {
            logger.LogWarning("[Taxonomy] AI returned out-of-list parent {Parent} for {Slug}; dropping.", parent, evt.Slug);
            return;
        }
        if (string.Equals(parent, evt.Slug, StringComparison.Ordinal)) return;

        await bus.InvokeAsync(new AssignServiceParent(evt.Slug, parent), ct);
        logger.LogInformation("[Taxonomy] Assigned {Slug} -> {Parent}", evt.Slug, parent);
    }
}
