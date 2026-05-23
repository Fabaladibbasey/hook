using Hook.Shared.Domain;

namespace Hook.Features.ServiceRequest;

public sealed record ServiceRequestCreatedEvent(Guid RequestId) : IDomainEvent;
