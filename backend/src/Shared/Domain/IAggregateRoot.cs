namespace Hook.Shared.Domain;

/// <summary>
/// Marker for aggregate-root entities. Implement directly when the entity
/// is a transactional consistency boundary but does not yet raise domain
/// events; extend <see cref="AggregateRoot"/> when the entity raises events
/// scraped by Wolverine's <c>PublishDomainEventsFromEntityFrameworkCore</c>.
/// </summary>
public interface IAggregateRoot;
