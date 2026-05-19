namespace Hook.Shared.Domain;

/// <summary>
/// Base for aggregate roots that stage domain events. Wolverine's
/// <c>DomainEventScraper&lt;AggregateRoot, IDomainEvent&gt;</c> drains the queue at
/// EF <c>SaveChanges</c> inside <c>AutoApplyTransactions</c> middleware and enrols
/// envelopes in the durable outbox.
/// </summary>
public abstract class AggregateRoot : IAggregateRoot
{
    private readonly List<IDomainEvent> _events = [];

    protected void RaiseDomainEvent(IDomainEvent evt) => _events.Add(evt);

    /// <summary>
    /// Intended for the Wolverine EF scraper only. Do NOT call from feature code —
    /// the scraper runs once at commit; a manual drain would dispatch events outside
    /// the outbox transaction.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }
}
