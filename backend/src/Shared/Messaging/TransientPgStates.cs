using Npgsql;

namespace Hook.Shared.Messaging;

internal static class TransientPgStates
{
    // Sub-second cooldowns fit: deadlock victims + serialization-failure retries
    // typically resolve in <100ms once Postgres releases the loser. Long cooldowns
    // here collapse the worker pool under storm.
    public static bool IsTransientFast(string? sqlState) => sqlState switch
    {
        PostgresErrorCodes.SerializationFailure => true,
        PostgresErrorCodes.DeadlockDetected => true,
        _ => false,
    };

    // Multi-second cooldowns fit: connection storms + too-many-connections need
    // real wait for the pool to recover.
    public static bool IsTransientSlow(string? sqlState) => sqlState switch
    {
        PostgresErrorCodes.TooManyConnections => true,
        PostgresErrorCodes.ConnectionException => true,
        PostgresErrorCodes.ConnectionFailure => true,
        _ => false,
    };

    public static bool IsTransient(string? sqlState) =>
        IsTransientFast(sqlState) || IsTransientSlow(sqlState);

    // Walks InnerException chain (EF wraps PostgresException in DbUpdateException;
    // some adapters wrap deeper still). Returns true if any layer matches.
    public static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && IsTransient(pg.SqlState))
                return true;
        }
        return false;
    }

    public static bool IsTransientFast(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && IsTransientFast(pg.SqlState))
                return true;
        }
        return false;
    }

    public static bool IsTransientSlow(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && IsTransientSlow(pg.SqlState))
                return true;
        }
        return false;
    }
}
