namespace Hook.Shared.Messaging;

// Wolverine's OnException predicate has no service-context overload — so the
// shutdown OCE policy needs an out-of-band signal.
//
// Production wiring: Program.cs subscribes Trip() to IHostApplicationLifetime
// .ApplicationStopping after app.Build(). That flips IsStopping at the start
// of graceful drain — well before Environment.HasShutdownStarted, which only
// turns true during AppDomain teardown (after Wolverine.StopAsync returns).
// Without the subscription, OCEs raised during the drain window DLQ instead
// of being discarded.
//
// Fallback: Environment.HasShutdownStarted stays in IsStopping as belt-and-
// suspenders for OCEs that escape after late-shutdown finalizer paths where
// the subscription has already disposed.
//
// Tests: drive the gate directly via Trip()/Reset() for the ShutdownOce policy
// path. Parallel test fixtures share the process, so one fixture's host
// stopping can race-trip the gate for an unrelated fixture's still-running
// tests. The race window is bounded (only WolverineErrorPolicyTests cares about
// the gate's state) and that test resets defensively in its ctor.
internal static class WolverineShutdownGate
{
    private static int _stopping;

    public static bool IsStopping =>
        Volatile.Read(ref _stopping) == 1 || Environment.HasShutdownStarted;

    public static void Trip() => Interlocked.Exchange(ref _stopping, 1);

    internal static void Reset() => Interlocked.Exchange(ref _stopping, 0);
}
