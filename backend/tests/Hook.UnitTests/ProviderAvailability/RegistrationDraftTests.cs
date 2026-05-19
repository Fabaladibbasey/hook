using Hook.Features.ProviderAvailability.Register;
using Shouldly;

namespace Hook.UnitTests.ProviderAvailability;

public class RegistrationDraftTests
{
    [Fact]
    public void ReplaceStateFrom_DraftServices_IsIndependentCopy()
    {
        // SetServices reassigns DraftServices, so referential aliasing is the only
        // way the old code could leak mutation. Pin that invariant directly.
        var now = DateTimeOffset.Parse("2026-05-01T12:00:00Z");
        var source = RegistrationDraft.Start("+2203331234", now);
        source.SetServices(new[] { "plumbing", "carpentry" }, now);

        var target = RegistrationDraft.Start("+2203339999", now);
        target.ReplaceStateFrom(source);

        target.DraftServices.ShouldBe(new[] { "plumbing", "carpentry" });
        ReferenceEquals(target.DraftServices, source.DraftServices).ShouldBeFalse();
    }
}
