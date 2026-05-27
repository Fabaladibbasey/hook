using Hook.Features.Ai.PlatformQa;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Hook.IntegrationTests.Ai.PlatformQa;

[Collection("Pipeline-4")]
public sealed class PlatformAnswerDedupGateTests : PipelineTestBase
{
    public PlatformAnswerDedupGateTests(DevPipelineFixture fx) : base(fx) { }

    private const int WindowSeconds = 60;

    private static string UniquePhone() => $"+220{Random.Shared.Next(0, 10_000_000):D7}";

    private static (PlatformAnswerDedupGate Gate, FakeTimeProvider Clock, HookDbContext Db) BuildGate(
        IServiceProvider sp, DateTimeOffset? start = null)
    {
        var db = sp.GetRequiredService<HookDbContext>();
        var opts = Options.Create(new PlatformAnswerOptions { DedupWindowSeconds = WindowSeconds });
        var clock = new FakeTimeProvider(start ?? DateTimeOffset.UtcNow);
        return (new PlatformAnswerDedupGate(db, opts, clock), clock, db);
    }

    [Fact]
    public async Task TryClaim_FirstCall_ReturnsTrue()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var (gate, _, _) = BuildGate(scope.ServiceProvider);
        var phone = UniquePhone();

        var claimed = await gate.TryClaimAsync(phone, 12345L, CancellationToken.None);

        Assert.True(claimed);
    }

    [Fact]
    public async Task TryClaim_SecondCallSameHashInsideWindow_ReturnsFalse()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var (gate, clock, _) = BuildGate(scope.ServiceProvider);
        var phone = UniquePhone();
        const long hash = 555L;

        Assert.True(await gate.TryClaimAsync(phone, hash, CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(WindowSeconds - 5));
        var second = await gate.TryClaimAsync(phone, hash, CancellationToken.None);

        Assert.False(second);
    }

    [Fact]
    public async Task TryClaim_SecondCallSameHashPastWindow_ReturnsTrueAndRefreshesStamp()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var (gate, clock, db) = BuildGate(scope.ServiceProvider);
        var phone = UniquePhone();
        const long hash = 777L;

        Assert.True(await gate.TryClaimAsync(phone, hash, CancellationToken.None));
        var firstStamp = await db.PlatformAnswerDedup.AsNoTracking()
            .Where(d => d.Phone == phone && d.QuestionHash == hash)
            .Select(d => d.AnsweredAt)
            .SingleAsync();

        clock.Advance(TimeSpan.FromSeconds(WindowSeconds + 1));
        var second = await gate.TryClaimAsync(phone, hash, CancellationToken.None);

        Assert.True(second);
        var refreshed = await db.PlatformAnswerDedup.AsNoTracking()
            .Where(d => d.Phone == phone && d.QuestionHash == hash)
            .Select(d => d.AnsweredAt)
            .SingleAsync();
        Assert.True(refreshed > firstStamp, $"Expected refreshed stamp > {firstStamp:o}, got {refreshed:o}");
    }

    [Fact]
    public async Task TryClaim_DifferentHashSamePhoneSameWindow_ReturnsTrue()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var (gate, clock, _) = BuildGate(scope.ServiceProvider);
        var phone = UniquePhone();

        Assert.True(await gate.TryClaimAsync(phone, 111L, CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(WindowSeconds - 1));
        var other = await gate.TryClaimAsync(phone, 222L, CancellationToken.None);

        Assert.True(other, "Different question hash must not be blocked by another row's window.");
    }

    [Fact]
    public async Task TryClaim_TwoConcurrentCallsSameKey_ExactlyOneWins()
    {
        // INSERT ... ON CONFLICT ... RETURNING is atomic per row. Two parallel
        // claimants on the same (phone, hash) must produce exactly one true,
        // because only one can be the INSERTer and the post-conflict UPDATE
        // sees a fresh AnsweredAt that fails the WHERE-stale predicate.
        var sp = _fx.Factory.Services;
        var phone = UniquePhone();
        const long hash = unchecked((long)0xCAFEBABE);

        var taskA = Task.Run(async () =>
        {
            await using var s = sp.CreateAsyncScope();
            var (gate, _, _) = BuildGate(s.ServiceProvider);
            return await gate.TryClaimAsync(phone, hash, CancellationToken.None);
        });
        var taskB = Task.Run(async () =>
        {
            await using var s = sp.CreateAsyncScope();
            var (gate, _, _) = BuildGate(s.ServiceProvider);
            return await gate.TryClaimAsync(phone, hash, CancellationToken.None);
        });

        var (a, b) = (await taskA, await taskB);

        Assert.True(a ^ b, $"Exactly one of two concurrent claims must win. Got A={a} B={b}.");
    }
}
