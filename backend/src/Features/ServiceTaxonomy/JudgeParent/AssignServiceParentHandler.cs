using Hook.Features.ServiceTaxonomy.ServiceAggregate;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class AssignServiceParentHandler(IServiceRepository repository)
{
    // Default-transactional: AutoApplyTransactions commits the AssignParent
    // mutation at handler end. Idempotent on re-fire — already-parented svc is a no-op.
    public async Task Handle(AssignServiceParentCommand cmd, CancellationToken ct)
    {
        if (string.Equals(cmd.Slug, cmd.ParentSlug, StringComparison.Ordinal)) return;

        var svc = await repository.GetBySlugAsync(cmd.Slug, ct);
        if (svc is null || !svc.IsRoot) return;

        var parent = await repository.GetBySlugAsync(cmd.ParentSlug, ct);
        if (parent is null || !parent.IsRoot) return;

        svc.AssignParent(parent);
    }
}
