using Hook.Features.Tips;
using Shouldly;

namespace Hook.UnitTests.Tips;

// TipTrigger rides the durable outbox via SendWhatsAppTextCommand.Tip and
// RecordTipCooldownCommand.Trigger. Reordering or removing a member would
// dead-letter in-flight envelopes that serialised under the old ordinals.
// Sibling to IntentKindOrdinalsTests.
public class TipTriggerOrdinalsTests
{
    [Fact]
    public void Ordinals_StableAppendOnly()
    {
        var actual = Enum.GetValues<TipTrigger>()
            .OrderBy(v => (int)v)
            .Select(v => (v.ToString(), (int)v))
            .ToArray();

        var expected = new[]
        {
            ("AfterWelcome", 0),
            ("AfterMatchPresented", 1),
            ("AfterContactShared", 2),
            ("AfterChatOpened", 3),
            ("AfterDraftDone", 4),
            ("UserRequested", 5),
        };

        actual.ShouldBe(expected);
    }
}
