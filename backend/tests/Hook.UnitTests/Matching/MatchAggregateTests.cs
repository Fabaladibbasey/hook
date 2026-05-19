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
}
