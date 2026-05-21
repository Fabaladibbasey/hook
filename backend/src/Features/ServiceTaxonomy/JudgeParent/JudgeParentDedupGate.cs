using Hook.Shared.Persistence;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public interface IJudgeParentDedupGate
{
    Task<bool> TryClaimAsync(string slug, CancellationToken ct);
}

public sealed class JudgeParentDedupGate(HookDbContext db, TimeProvider clock) : IJudgeParentDedupGate
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public async Task<bool> TryClaimAsync(string slug, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await db.TryInsertUniqueAsync(JudgeParentDedup.Stamp(slug, now), ct, JudgeParentDedupConstants.PrimaryKeyName))
            return true;

        var cutoff = now - Window;
        var refreshed = await db.JudgeParentDedups
            .Where(d => d.Slug == slug && d.JudgedAt <= cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.JudgedAt, now), ct);
        return refreshed > 0;
    }
}
