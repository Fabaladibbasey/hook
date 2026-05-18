namespace Hook.Shared.Domain;

/// <summary>
/// Base for aggregates that stage domain events; Wolverine's <c>DomainEventScraper</c>
/// dequeues at EF <c>SaveChanges</c> and publishes via the durable outbox.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _events = [];

    protected void RaiseDomainEvent(IDomainEvent evt) => _events.Add(evt);

    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        if (_events.Count == 0) return [];
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }
}
