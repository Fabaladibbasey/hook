namespace Hook.Features.ProviderAvailability.AvailabilityAggregate;

public interface IProviderAvailabilityRepository
{
    Task<ProviderAvailability?> GetAsync(string phone, CancellationToken ct = default);
    Task AddAsync(ProviderAvailability availability, CancellationToken ct = default);
    Task RemoveAsync(string phone, CancellationToken ct = default);
}
