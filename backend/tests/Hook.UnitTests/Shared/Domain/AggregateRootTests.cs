using Hook.Shared.Domain;
using Shouldly;

namespace Hook.UnitTests.Shared.Domain;

public class AggregateRootTests
{
    private sealed record TestEvent(int Tag) : IDomainEvent;

    private sealed class TestAggregate : AggregateRoot
    {
        public void Raise(IDomainEvent evt) => RaiseDomainEvent(evt);
    }

    [Fact]
    public void DequeueEvents_NewAggregate_ReturnsEmpty()
    {
        new TestAggregate().DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public void DequeueEvents_AfterRaise_ReturnsEventThenEmpty()
    {
        var agg = new TestAggregate();
        var evt = new TestEvent(1);
        agg.Raise(evt);

        var first = agg.DequeueEvents();
        first.Count.ShouldBe(1);
        first[0].ShouldBe(evt);

        agg.DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public void DequeueEvents_MultipleRaises_PreservesOrder()
    {
        var agg = new TestAggregate();
        agg.Raise(new TestEvent(1));
        agg.Raise(new TestEvent(2));
        agg.Raise(new TestEvent(3));

        var drained = agg.DequeueEvents();
        drained.Select(e => ((TestEvent)e).Tag).ShouldBe([1, 2, 3]);
    }
}
