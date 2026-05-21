using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hook.IntegrationTests.ServiceTaxonomy;

[Collection("Pipeline-Migration")]
public sealed class JudgeParentDedupGateTests : PipelineTestBase
{
    public JudgeParentDedupGateTests(DevPipelineFixture fx) : base(fx) { }

    private async Task<bool> ClaimAsync(string slug, FakeTimeProvider clock)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var gate = new JudgeParentDedupGate(db, clock);
        return await gate.TryClaimAsync(slug, CancellationToken.None);
    }

    private async Task<DateTimeOffset> ReadStampAsync(string slug)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var row = await db.JudgeParentDedups.SingleAsync(d => d.Slug == slug);
        return row.JudgedAt;
    }

    // Postgres timestamptz stores microseconds; FakeTimeProvider produces sub-microsecond
    // ticks. Use a small tolerance instead of exact equality.
    private static readonly TimeSpan StampTolerance = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task TryClaimAsync_FirstCall_InsertsRow_AndReturnsTrue()
    {
        var slug = $"welding-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        (await ClaimAsync(slug, clock)).ShouldBeTrue();

        (await ReadStampAsync(slug)).ShouldBe(clock.GetUtcNow(), StampTolerance);
    }

    [Fact]
    public async Task TryClaimAsync_DuplicateWithinWindow_ReturnsFalse_AndDoesNotRefresh()
    {
        var slug = $"welding-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        (await ClaimAsync(slug, clock)).ShouldBeTrue();
        var firstStamp = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(1));
        (await ClaimAsync(slug, clock)).ShouldBeFalse();
        (await ClaimAsync(slug, clock)).ShouldBeFalse();

        (await ReadStampAsync(slug)).ShouldBe(firstStamp, StampTolerance);
    }

    [Fact]
    public async Task TryClaimAsync_AfterWindowElapsed_RefreshesStamp_AndReturnsTrue()
    {
        var slug = $"welding-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        (await ClaimAsync(slug, clock)).ShouldBeTrue();

        clock.Advance(JudgeParentDedupGate.Window + TimeSpan.FromSeconds(1));
        var laterNow = clock.GetUtcNow();
        (await ClaimAsync(slug, clock)).ShouldBeTrue();

        (await ReadStampAsync(slug)).ShouldBe(laterNow, StampTolerance);
    }
}
