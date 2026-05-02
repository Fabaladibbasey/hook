using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ProviderAvailability.Dev;

public static class DevProviderEndpoints
{
    public static IEndpointRouteBuilder MapDevProviders(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/dev/providers");

        group.MapPost("/seed", async (DevProviderSeeder seeder, CancellationToken ct) =>
        {
            var report = await seeder.SeedAsync(ct);
            return Results.Ok(report);
        });

        group.MapGet("/", async (HookDbContext db, CancellationToken ct) =>
        {
            var entities = await db.ProviderAvailabilities
                .OrderBy(p => p.Phone)
                .ToListAsync(ct);

            var rows = entities.Select(p => new
            {
                p.Phone,
                p.Services,
                p.FormattedAddress,
                p.ShareContact,
                Latitude = p.Location.Y,
                Longitude = p.Location.X,
                p.LastActiveAt,
                p.ExpiresAt
            });
            return Results.Ok(rows);
        });

        return routes;
    }
}
