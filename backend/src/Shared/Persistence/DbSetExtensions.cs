using Microsoft.EntityFrameworkCore;

namespace Hook.Shared.Persistence;

public static class DbSetExtensions
{
    public static async Task UpsertAsync<T>(
        this DbSet<T> set,
        object[] keyValues,
        T incoming,
        Action<T, T> applyTo,
        CancellationToken ct = default) where T : class
    {
        var existing = await set.FindAsync(keyValues, ct);
        if (existing is null)
        {
            await set.AddAsync(incoming, ct);
        }
        else if (!ReferenceEquals(existing, incoming))
        {
            applyTo(existing, incoming);
        }
    }

    public static async Task<bool> DeleteByKeyAsync<T>(
        this DbSet<T> set,
        object[] keyValues,
        CancellationToken ct = default) where T : class
    {
        var existing = await set.FindAsync(keyValues, ct);
        if (existing is null) return false;
        set.Remove(existing);
        return true;
    }

    /// <summary>
    /// Insert <paramref name="entity"/> and SaveChanges; return false if a 23505
    /// (unique violation) on any of <paramref name="constraintNames"/> raced us.
    /// Uses a savepoint when an outer transaction is present, so a lost race does
    /// not poison the enclosing Wolverine handler transaction. Pass multiple names
    /// when one insert can race against more than one partial unique index.
    /// </summary>
    public static async Task<bool> TryInsertUniqueAsync<T>(
        this DbContext db,
        T entity,
        IReadOnlyList<string> constraintNames,
        CancellationToken ct = default) where T : class
    {
        var outer = db.Database.CurrentTransaction;
        var savepoint = outer is null ? null : $"try_insert_{Guid.NewGuid():N}";
        if (savepoint is not null && outer is not null)
            await outer.CreateSavepointAsync(savepoint, ct);

        await db.AddAsync(entity, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            if (savepoint is not null && outer is not null)
                await outer.ReleaseSavepointAsync(savepoint, ct);
            return true;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && constraintNames.Contains(pg.ConstraintName))
        {
            db.Entry(entity).State = EntityState.Detached;
            if (savepoint is not null && outer is not null)
                await outer.RollbackToSavepointAsync(savepoint, ct);
            return false;
        }
    }
}
