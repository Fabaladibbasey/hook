namespace Hook.Shared.Messaging;

// Wolverine's OnException predicate has no service-context overload — so the
// shutdown OCE policy needs an out-of-band signal.
//
// Production: relies on Environment.HasShutdownStarted, which flips during the
// AppDomain teardown that follows a real host shutdown.
//
// Tests: drive the gate directly via Trip()/Reset() for the ShutdownOce policy
// path. We avoid an IHostApplicationLifetime.ApplicationStopping subscription
// because parallel test fixtures share the process — one fixture's host
// stopping would race-trip the gate for an unrelated fixture's still-running
// tests.
internal static class WolverineShutdownGate
{
    private static int _stopping;

    public static bool IsStopping =>
        Volatile.Read(ref _stopping) == 1 || Environment.HasShutdownStarted;

    public static void Trip() => Interlocked.Exchange(ref _stopping, 1);

    internal static void Reset() => Interlocked.Exchange(ref _stopping, 0);
}
