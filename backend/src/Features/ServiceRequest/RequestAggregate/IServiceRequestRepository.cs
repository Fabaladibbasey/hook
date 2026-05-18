namespace Hook.Features.ServiceRequest.RequestAggregate;

public interface IServiceRequestRepository
{
    Task<ServiceRequest?> GetActiveByClientAsync(string clientPhone, CancellationToken ct = default);
    Task<ServiceRequest?> GetAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(ServiceRequest request, CancellationToken ct = default);
}
