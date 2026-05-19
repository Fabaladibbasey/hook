using Hook.Shared.Domain;

namespace Hook.Features.ServiceRequest;

public sealed record ServiceRequestCreated(Guid RequestId) : IDomainEvent;
