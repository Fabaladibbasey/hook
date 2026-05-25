using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Shouldly;

namespace Hook.UnitTests.ServiceTaxonomy;

public class ServiceTests
{
    [Fact]
    public void RememberRawExample_TruncatesEntries_BeyondMaxLength()
    {
        var svc = Service.Create("plumbing", DateTimeOffset.UtcNow);
        var raw = new string('x', Service.MaxRawExampleLength + 50);

        svc.RememberRawExample(raw);

        svc.RawExamples.ShouldHaveSingleItem();
        svc.RawExamples[0].Length.ShouldBe(Service.MaxRawExampleLength);
    }

    [Fact]
    public void RememberRawExample_KeepsUpToTenEntries_DroppingOldest()
    {
        var svc = Service.Create("plumbing", DateTimeOffset.UtcNow);
        for (var i = 0; i < 12; i++) svc.RememberRawExample($"entry-{i}");

        svc.RawExamples.Count.ShouldBe(10);
        svc.RawExamples[0].ShouldBe("entry-2");
        svc.RawExamples[9].ShouldBe("entry-11");
    }

    [Fact]
    public void RememberRawExample_DeduplicatesCaseInsensitive()
    {
        var svc = Service.Create("plumbing", DateTimeOffset.UtcNow);
        svc.RememberRawExample("Leaky pipe");
        svc.RememberRawExample("leaky pipe");

        svc.RawExamples.ShouldHaveSingleItem();
    }

    [Fact]
    public void AssignParent_RejectsReParent_OnAlreadyParented()
    {
        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        var doctor = Service.Create("doctor", DateTimeOffset.UtcNow);
        var lawyer = Service.Create("lawyer", DateTimeOffset.UtcNow);
        svc.AssignParent(doctor);

        Should.Throw<InvalidOperationException>(() => svc.AssignParent(lawyer));
        svc.ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public void AssignParent_RejectsSelfParent()
    {
        var svc = Service.Create("doctor", DateTimeOffset.UtcNow);
        Should.Throw<InvalidOperationException>(() => svc.AssignParent(svc));
        svc.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public void AssignParent_RejectsNullParent()
    {
        var svc = Service.Create("doctor", DateTimeOffset.UtcNow);
        Should.Throw<ArgumentNullException>(() => svc.AssignParent(null!));
    }

    [Fact]
    public void AssignParent_NonRootParent_Throws()
    {
        var grandparent = Service.Create("doctor", DateTimeOffset.UtcNow);
        var mid = Service.Create("internal-medicine", DateTimeOffset.UtcNow);
        mid.AssignParent(grandparent);

        var svc = Service.Create("cardiology", DateTimeOffset.UtcNow);
        Should.Throw<InvalidOperationException>(() => svc.AssignParent(mid));
        svc.ParentSlug.ShouldBeNull();
    }
}
