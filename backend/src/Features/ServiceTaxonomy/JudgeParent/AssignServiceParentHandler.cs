using Hook.Features.ServiceTaxonomy.ServiceAggregate;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class AssignServiceParentHandler(IServiceRepository repository)
{
    // Default-transactional: AutoApplyTransactions commits the AssignParent
    // mutation at handler end. Idempotent on re-fire — already-parented svc is a no-op.
    public async Task Handle(AssignServiceParent cmd, CancellationToken ct)
    {
        var svc = await repository.GetBySlugAsync(cmd.Slug, ct);
        if (svc is null || !svc.IsRoot) return;
        svc.AssignParent(cmd.ParentSlug);
    }
}
