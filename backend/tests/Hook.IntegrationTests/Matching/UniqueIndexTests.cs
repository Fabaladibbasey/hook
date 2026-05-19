using Hook.Features.Geocoding.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.IntegrationTests.Matching;

[Collection("Pipeline-4")]
public sealed class UniqueIndexTests : PipelineTestBase
{
    public UniqueIndexTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task Insert_DuplicateRequestProvider_RejectedByUniqueIndex()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var request = ServiceRequest.Create(
            $"+220{Guid.NewGuid().ToString("N")[..8]}", "plumbing",
            new Location(13.45, -16.6), "Banjul",
            $"req-{Guid.NewGuid()}", 5.0, DateTimeOffset.UtcNow, false);
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();

        var providerPhone = $"+220{Guid.NewGuid().ToString("N")[..8]}";
        db.Matches.Add(Match.Create(request.Id, providerPhone, "plumbing", 0, 0, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        db.Matches.Add(Match.Create(request.Id, providerPhone, "plumbing", 0, 0, DateTimeOffset.UtcNow));

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<Npgsql.PostgresException>()
            .SqlState.ShouldBe("23505");
    }
}
