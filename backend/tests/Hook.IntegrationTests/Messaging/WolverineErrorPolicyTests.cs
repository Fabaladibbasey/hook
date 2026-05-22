// NOTE: PolicyTest*Message records + handlers are registered into EVERY
// Pipeline-N shard's Wolverine handler discovery via DevPipelineFixture's
// IncludeAssembly(typeof(DevPipelineFixture).Assembly) call. To prevent
// cross-test pollution, NO OTHER test in this repo should publish these
// messages. The pre/post invocation-count deltas below also harden against
// accidental cross-fire.
using Hook.Shared.Messaging;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Tracking;

namespace Hook.IntegrationTests.Messaging;

public sealed record PolicyTestOceMessage(Guid Id);
public sealed record PolicyTestTransientPgMessage(Guid Id);
public sealed record PolicyTestTransientPgSlowMessage(Guid Id);
public sealed record PolicyTestEfWrappedPgMessage(Guid Id);

public static class PolicyTestOceHandler
{
    public static int InvocationCount;
    public static Task Handle(PolicyTestOceMessage _)
    {
        Interlocked.Increment(ref InvocationCount);
        throw new OperationCanceledException("policy-test-oce");
    }
}

public static class PolicyTestTransientPgHandler
{
    public static int InvocationCount;
    public static Task Handle(PolicyTestTransientPgMessage _)
    {
        Interlocked.Increment(ref InvocationCount);
        throw new PostgresException(
            messageText: "policy-test-transient",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.SerializationFailure);
    }
}

public static class PolicyTestTransientPgSlowHandler
{
    public static int InvocationCount;
    public static Task Handle(PolicyTestTransientPgSlowMessage _)
    {
        Interlocked.Increment(ref InvocationCount);
        throw new PostgresException(
            messageText: "policy-test-transient-slow",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.TooManyConnections);
    }
}

public static class PolicyTestEfWrappedPgHandler
{
    public static int InvocationCount;
    public static Task Handle(PolicyTestEfWrappedPgMessage _)
    {
        Interlocked.Increment(ref InvocationCount);
        var pg = new PostgresException(
            messageText: "ef-wrapped-transient",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.SerializationFailure);
        throw new DbUpdateException("ef-wrap", pg);
    }
}

[Collection("Pipeline-1")]
public sealed class WolverineErrorPolicyTests : PipelineTestBase
{
    public WolverineErrorPolicyTests(DevPipelineFixture fx) : base(fx)
    {
        // Defensive reset — a prior test in the process may have tripped the
        // shutdown gate, which would otherwise force OCE-discard here and break
        // HandlerLocalOperationCanceled... assertions.
        WolverineShutdownGate.Reset();
    }

    [Fact]
    public async Task HandlerLocalOperationCanceled_FallsThroughToDeadLetter_NotDiscarded()
    {
        WolverineShutdownGate.Reset();
        var startCount = PolicyTestOceHandler.InvocationCount;
        var host = _fx.Factory.Services.GetRequiredService<IHost>();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(15))
            .PublishMessageAndWaitAsync(new PolicyTestOceMessage(Guid.NewGuid()));

        await EventuallyAsync(async () =>
            (await CountDeadLettersAsync(typeof(PolicyTestOceMessage).FullName!)).ShouldBe(1,
                "Handler-local OCE (not host-shutdown) must surface as a DLQ row, not silently discard."));
        (PolicyTestOceHandler.InvocationCount - startCount).ShouldBe(1);
    }

    [Fact]
    public async Task ShutdownOce_Discarded_NoDeadLetter()
    {
        // Trip the gate to simulate IHostApplicationLifetime.ApplicationStopping
        // firing. The OCE policy must now Discard rather than DLQ.
        WolverineShutdownGate.Trip();
        try
        {
            var startCount = PolicyTestOceHandler.InvocationCount;
            var host = _fx.Factory.Services.GetRequiredService<IHost>();

            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(TimeSpan.FromSeconds(15))
                .PublishMessageAndWaitAsync(new PolicyTestOceMessage(Guid.NewGuid()));

            (PolicyTestOceHandler.InvocationCount - startCount).ShouldBe(1);
            // Discard semantics: no DLQ row even on a long settle deadline.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await CountDeadLettersAsync(typeof(PolicyTestOceMessage).FullName!)).ShouldBe(0,
                "Shutdown-OCE must Discard, not DLQ.");
        }
        finally
        {
            WolverineShutdownGate.Reset();
        }
    }

    [Fact]
    public async Task TransientPostgresFast_RetriedThreeTimes_ThenDeadLettered()
    {
        WolverineShutdownGate.Reset();
        var startCount = PolicyTestTransientPgHandler.InvocationCount;
        var host = _fx.Factory.Services.GetRequiredService<IHost>();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(new PolicyTestTransientPgMessage(Guid.NewGuid()));

        await EventuallyAsync(async () =>
            (await CountDeadLettersAsync(typeof(PolicyTestTransientPgMessage).FullName!)).ShouldBe(1));
        (PolicyTestTransientPgHandler.InvocationCount - startCount).ShouldBe(4,
            "Initial + 3 fast-tier RetryWithCooldown attempts");
    }

    [Fact]
    public async Task TransientPostgresSlow_RetriedThreeTimes_ThenDeadLettered()
    {
        WolverineShutdownGate.Reset();
        var startCount = PolicyTestTransientPgSlowHandler.InvocationCount;
        var host = _fx.Factory.Services.GetRequiredService<IHost>();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(60))
            .PublishMessageAndWaitAsync(new PolicyTestTransientPgSlowMessage(Guid.NewGuid()));

        await EventuallyAsync(async () =>
            (await CountDeadLettersAsync(typeof(PolicyTestTransientPgSlowMessage).FullName!)).ShouldBe(1));
        (PolicyTestTransientPgSlowHandler.InvocationCount - startCount).ShouldBe(4,
            "Initial + 3 slow-tier RetryWithCooldown attempts");
    }

    [Fact]
    public async Task EfWrappedPostgresException_RetriedAndDeadLettered()
    {
        // EF Core wraps PostgresException in DbUpdateException on SaveChanges.
        // The retry policy must walk InnerException so realistic deadlocks retry.
        WolverineShutdownGate.Reset();
        var startCount = PolicyTestEfWrappedPgHandler.InvocationCount;
        var host = _fx.Factory.Services.GetRequiredService<IHost>();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(new PolicyTestEfWrappedPgMessage(Guid.NewGuid()));

        await EventuallyAsync(async () =>
            (await CountDeadLettersAsync(typeof(PolicyTestEfWrappedPgMessage).FullName!)).ShouldBe(1));
        (PolicyTestEfWrappedPgHandler.InvocationCount - startCount).ShouldBe(4,
            "Initial + 3 fast-tier attempts via inner-exception walk");
    }

    private async Task<long> CountDeadLettersAsync(string messageTypeFullName)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from wolverine.wolverine_dead_letters where message_type = @t";
        cmd.Parameters.AddWithValue("t", messageTypeFullName);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // DLQ insert is async after the Discard/final enrollment — poll with a deadline
    // instead of trusting PublishMessageAndWaitAsync to know the cascade settled.
    private static async Task EventuallyAsync(Func<Task> probe,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var step = interval ?? TimeSpan.FromMilliseconds(100);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await probe();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(step);
            }
        }
        if (last is not null) throw last;
    }
}
