using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.MatchAggregate;
using Shouldly;

namespace Hook.UnitTests.Matching;

public sealed class MatchAggregateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Match NewMatch() =>
        Match.Create(
            requestId: Guid.NewGuid(),
            providerPhone: "+220300001",
            serviceSlug: "plumbing",
            distanceKm: 1.0,
            score: 0.5,
            now: Now);

    [Fact]
    public void ClaimForPickup_WhenAlreadyPicked_Throws()
    {
        var m = NewMatch();
        m.ClaimForPickup(contactShared: true, now: Now);

        Should.Throw<InvalidOperationException>(
                () => m.ClaimForPickup(contactShared: false, now: Now.AddSeconds(1)))
            .Message.ShouldContain("already picked");
    }

    [Fact]
    public void ClaimForChat_WhenAlreadyRouted_Throws()
    {
        var m = NewMatch();
        m.ClaimForChat(Guid.NewGuid());

        Should.Throw<InvalidOperationException>(
                () => m.ClaimForChat(Guid.NewGuid()))
            .Message.ShouldContain("already routed");
    }

    [Fact]
    public void Create_StampsCreatedAt_AndLeavesUnclaimed()
    {
        var m = NewMatch();

        m.CreatedAt.ShouldBe(Now);
        m.Id.ShouldNotBe(Guid.Empty);
        m.PickedAt.ShouldBeNull();
        m.ChatId.ShouldBeNull();
        m.ContactShared.ShouldBeFalse();
    }

    [Fact]
    public void ClaimForPickup_FirstCall_RecordsPickAtAndConsent()
    {
        var m = NewMatch();
        var pickedAt = Now.AddMinutes(5);

        m.ClaimForPickup(contactShared: true, now: pickedAt);

        m.PickedAt.ShouldBe(pickedAt);
        m.ContactShared.ShouldBeTrue();
    }

    [Fact]
    public void ClaimForChat_FirstCall_StampsChatId()
    {
        var m = NewMatch();
        var chatId = Guid.NewGuid();

        m.ClaimForChat(chatId);

        m.ChatId.ShouldBe(chatId);
    }

    [Fact]
    public void MarkContactExchanged_BeforeClaim_Throws()
    {
        var m = NewMatch();

        Should.Throw<InvalidOperationException>(
                () => m.MarkContactExchanged(m.RequestId, "+2203339999", m.ProviderPhone))
            .Message.ShouldContain("before claim");
    }

    [Fact]
    public void MarkContactExchanged_AfterClaim_RaisesContactExchangedEvent()
    {
        var m = NewMatch();
        m.ClaimForPickup(contactShared: true, now: Now);

        m.MarkContactExchanged(m.RequestId, "+2203339999", m.ProviderPhone);

        var raised = m.DequeueEvents();
        raised.ShouldHaveSingleItem();
        var evt = raised[0].ShouldBeOfType<ContactExchangedEvent>();
        evt.MatchId.ShouldBe(m.Id);
        evt.RequestId.ShouldBe(m.RequestId);
        evt.ProviderPhone.ShouldBe(m.ProviderPhone);
    }
}
