using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.ServiceRequest.RequestAggregate;

public sealed class ServiceRequestRepository(HookDbContext db) : IServiceRequestRepository
{
    public Task<ServiceRequest?> GetActiveByClientAsync(string clientPhone, CancellationToken ct = default) =>
        db.ServiceRequests
            .Where(r => r.ClientPhone == clientPhone && r.Status != ServiceRequestStatus.Closed)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<ServiceRequest?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.ServiceRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task AddAsync(ServiceRequest request, CancellationToken ct = default) =>
        await db.ServiceRequests.AddAsync(request, ct);
}
