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

    /// <summary>Insert and SaveChanges; return false on a 23505 against any of
    /// <paramref name="constraintNames"/>. The savepoint wrapper isolates the race
    /// from the enclosing Wolverine handler transaction.</summary>
    public static Task<bool> TryInsertUniqueAsync<T>(
        this DbContext db,
        T entity,
        CancellationToken ct = default,
        params string[] constraintNames) where T : class =>
        TryInsertUniqueAsync(db, entity, constraintNames, ct);

    public static async Task<bool> TryInsertUniqueAsync<T>(
        this DbContext db,
        T entity,
        IReadOnlyList<string> constraintNames,
        CancellationToken ct = default) where T : class
    {
        var outer = db.Database.CurrentTransaction;
        string? savepoint = null;
        if (outer is not null)
        {
            savepoint = $"sp{Interlocked.Increment(ref _savepointCounter)}";
            await outer.CreateSavepointAsync(savepoint, ct);
        }

        await db.AddAsync(entity, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            if (savepoint is not null) await outer!.ReleaseSavepointAsync(savepoint, ct);
            return true;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && constraintNames.Contains(pg.ConstraintName))
        {
            db.Entry(entity).State = EntityState.Detached;
            if (savepoint is not null) await outer!.RollbackToSavepointAsync(savepoint, ct);
            return false;
        }
    }

    private static long _savepointCounter;
}
