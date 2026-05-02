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
}
