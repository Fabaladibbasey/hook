using Hook.Features.Ai.Models;
using Shouldly;

namespace Hook.UnitTests.Ai;

// IntentKind is serialised as ordinal int through Wolverine outbox envelopes
// (ClassifyInboundIntentCommand) and on-disk classifier metadata. Reordering
// or inserting a value mid-list silently corrupts in-flight envelopes across
// a deploy. This snapshot is the source-of-truth pair set — bump it only by
// APPENDING a new (name, ordinal) line.
public class IntentKindOrdinalsTests
{
    [Fact]
    public void Ordinals_MatchSnapshot()
    {
        var actual = Enum.GetValues<IntentKind>()
            .Select(v => (Name: v.ToString(), Ordinal: (int)v))
            .OrderBy(t => t.Ordinal)
            .ToArray();

        var expected = new (string Name, int Ordinal)[]
        {
            ("Unknown", 0),
            ("ProviderRegistration", 1),
            ("ServiceRequest", 2),
            ("MatchSelection", 3),
            ("NextMatches", 4),
            ("IncreaseRange", 5),
            ("ShareContact", 6),
            ("Confirmation", 7),
            ("Rejection", 8),
            ("Edit", 9),
            ("Cancel", 10),
            ("FeedbackResponse", 11),
            ("Greeting", 12),
            ("NewRequest", 13),
            ("PlatformQuestion", 14),
        };

        actual.ShouldBe(expected);
    }
}
