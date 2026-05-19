using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Shouldly;

namespace Hook.UnitTests.ServiceTaxonomy;

public class ServiceTests
{
    [Fact]
    public void RememberRawExample_TruncatesEntries_BeyondMaxLength()
    {
        var svc = Service.Create("plumbing");
        var raw = new string('x', Service.MaxRawExampleLength + 50);

        svc.RememberRawExample(raw);

        svc.RawExamples.ShouldHaveSingleItem();
        svc.RawExamples[0].Length.ShouldBe(Service.MaxRawExampleLength);
    }

    [Fact]
    public void RememberRawExample_KeepsUpToTenEntries_DroppingOldest()
    {
        var svc = Service.Create("plumbing");
        for (var i = 0; i < 12; i++) svc.RememberRawExample($"entry-{i}");

        svc.RawExamples.Count.ShouldBe(10);
        svc.RawExamples[0].ShouldBe("entry-2");
        svc.RawExamples[9].ShouldBe("entry-11");
    }

    [Fact]
    public void RememberRawExample_DeduplicatesCaseInsensitive()
    {
        var svc = Service.Create("plumbing");
        svc.RememberRawExample("Leaky pipe");
        svc.RememberRawExample("leaky pipe");

        svc.RawExamples.ShouldHaveSingleItem();
    }

    [Fact]
    public void AssignParent_RejectsReParent_OnAlreadyParented()
    {
        var svc = Service.Create("cardiology");
        var doctor = Service.Create("doctor");
        var lawyer = Service.Create("lawyer");
        svc.AssignParent(doctor);

        Should.Throw<InvalidOperationException>(() => svc.AssignParent(lawyer));
        svc.ParentSlug.ShouldBe("doctor");
    }

    [Fact]
    public void AssignParent_RejectsSelfParent()
    {
        var svc = Service.Create("doctor");
        Should.Throw<InvalidOperationException>(() => svc.AssignParent(svc));
        svc.ParentSlug.ShouldBeNull();
    }

    [Fact]
    public void AssignParent_RejectsNullParent()
    {
        var svc = Service.Create("doctor");
        Should.Throw<ArgumentNullException>(() => svc.AssignParent(null!));
    }

    [Fact]
    public void AssignParent_NonRootParent_Throws()
    {
        var grandparent = Service.Create("doctor");
        var mid = Service.Create("internal-medicine");
        mid.AssignParent(grandparent);

        var svc = Service.Create("cardiology");
        Should.Throw<InvalidOperationException>(() => svc.AssignParent(mid));
        svc.ParentSlug.ShouldBeNull();
    }
}
