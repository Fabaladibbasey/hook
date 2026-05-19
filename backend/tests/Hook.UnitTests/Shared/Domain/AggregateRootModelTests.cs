using Hook.Shared.Domain;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Hook.UnitTests.Shared.Domain;

public sealed class AggregateRootModelTests
{
    [Fact]
    public void EfModel_DoesNotMap_Events_OnAnyAggregateRoot()
    {
        var opts = new DbContextOptionsBuilder<HookDbContext>()
            .UseNpgsql("Host=localhost;Database=stub;Username=stub;Password=stub",
                npgsql => npgsql.UseNetTopologySuite())
            .Options;
        using var db = new HookDbContext(opts);

        var offenders = db.Model.GetEntityTypes()
            .Where(t => typeof(IAggregateRoot).IsAssignableFrom(t.ClrType))
            .SelectMany(t => t.GetProperties()
                .Where(p => p.Name is "Events" or "DomainEvents" or "_events")
                .Select(p => $"{t.ClrType.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty(
            "AggregateRoot._events must never be mapped — Wolverine's scraper drains it; EF should not persist it.");
    }
}
