using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Microsoft.Extensions.Options;
using ProviderAvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.Features.ProviderAvailability.Dev;

public sealed record DevProviderSeedReport(
    int Seeded,
    int Refreshed,
    IReadOnlyList<string> Phones);

public sealed class DevProviderSeeder(
    IProviderAvailabilityRepository repository,
    IOptions<DevProviderSeedOptions> options,
    TimeProvider clock,
    ILogger<DevProviderSeeder> logger)
{
    public async Task<DevProviderSeedReport> SeedAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var ttl = TimeSpan.FromHours(opts.TtlHours);
        var now = clock.GetUtcNow();
        var specs = DevProviderSeedSet.All(opts);

        var seeded = 0;
        var refreshed = 0;
        var phones = new List<string>(specs.Count);

        foreach (var spec in specs)
        {
            var existing = await repository.GetAsync(spec.Phone, ct);
            if (existing is null)
            {
                var entity = ProviderAvailabilityEntity.Register(
                    spec.Phone,
                    spec.Services,
                    spec.Location,
                    spec.FormattedAddress,
                    spec.ShareContact,
                    ttl,
                    now);
                await repository.AddAsync(entity, ct);
                seeded++;
            }
            else
            {
                existing.UpdateServices(spec.Services);
                existing.UpdateLocation(spec.Location, spec.FormattedAddress);
                existing.UpdateConsent(spec.ShareContact);
                existing.Heartbeat(ttl, now);
                refreshed++;
            }
            phones.Add(spec.Phone);
        }

        await repository.SaveChangesAsync(ct);

        logger.LogInformation(
            "[DEV] Provider seed complete. Seeded={Seeded} Refreshed={Refreshed} Anchor=({Lat},{Lng})",
            seeded, refreshed, opts.ReferenceLat, opts.ReferenceLng);

        return new DevProviderSeedReport(seeded, refreshed, phones);
    }
}
