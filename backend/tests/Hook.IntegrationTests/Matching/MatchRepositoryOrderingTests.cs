using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.IntegrationTests.Matching;

[Collection("Pipeline-4")]
public sealed class MatchRepositoryOrderingTests : PipelineTestBase
{
    public MatchRepositoryOrderingTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task GetForRequestAsync_TiedScoreAndDistance_BreaksByCreatedAtThenId()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var request = await SeedRequestAsync(db);
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;

        var laterMatch = MatchEntity.Create(
            request.Id, $"+220{Guid.NewGuid().ToString("N")[..8]}", "plumbing",
            distanceKm: 1.0, score: 0.9, now: later);
        var earlierMatch = MatchEntity.Create(
            request.Id, $"+220{Guid.NewGuid().ToString("N")[..8]}", "plumbing",
            distanceKm: 1.0, score: 0.9, now: earlier);

        // Insert later first so the natural insertion order is the OPPOSITE of the
        // expected sort — proves the tertiary sort is doing the work.
        db.Matches.AddRange(laterMatch, earlierMatch);
        await db.SaveChangesAsync();

        var result = await repo.GetForRequestAsync(request.Id);

        result.Count.ShouldBe(2);
        result[0].ProviderPhone.ShouldBe(earlierMatch.ProviderPhone, "earlier CreatedAt should win the tie");
        result[1].ProviderPhone.ShouldBe(laterMatch.ProviderPhone);
    }

    [Fact]
    public async Task GetForRequestAsync_TiedScoreDistanceAndCreatedAt_BreaksById()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMatchRepository>();

        var request = await SeedRequestAsync(db);
        var sameTime = DateTimeOffset.UtcNow;
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        var higher = new MatchEntity
        {
            Id = higherId,
            RequestId = request.Id,
            ProviderPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}",
            ServiceSlug = "plumbing",
            DistanceKm = 1.0,
            Score = 0.9,
            CreatedAt = sameTime
        };
        var lower = new MatchEntity
        {
            Id = lowerId,
            RequestId = request.Id,
            ProviderPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}",
            ServiceSlug = "plumbing",
            DistanceKm = 1.0,
            Score = 0.9,
            CreatedAt = sameTime
        };

        db.Matches.AddRange(higher, lower);
        await db.SaveChangesAsync();

        var result = await repo.GetForRequestAsync(request.Id);

        result[0].Id.ShouldBe(lowerId, "lower Guid should win the final tie");
        result[1].Id.ShouldBe(higherId);
    }

    private static async Task<ServiceRequest> SeedRequestAsync(HookDbContext db)
    {
        var clientPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}";
        var request = ServiceRequest.Create(
            clientPhone, "plumbing",
            new Location(13.45, -16.6), "Banjul",
            $"req-{Guid.NewGuid()}", 5.0, DateTimeOffset.UtcNow, false);
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }
}
