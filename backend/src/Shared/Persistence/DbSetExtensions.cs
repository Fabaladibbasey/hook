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
    /// (unique violation) on <paramref name="constraintName"/> raced us. Wraps the
    /// "partial unique index + 23505 catch" idiom so call sites stay one-line.
    /// </summary>
    public static async Task<bool> TryInsertUniqueAsync<T>(
        this DbContext db,
        T entity,
        string constraintName,
        CancellationToken ct = default) where T : class
    {
        await db.AddAsync(entity, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName == constraintName)
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }
}
