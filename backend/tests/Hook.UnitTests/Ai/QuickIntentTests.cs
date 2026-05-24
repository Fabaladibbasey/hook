using Hook.Features.Ai;
using Hook.Features.Ai.Models;

namespace Hook.UnitTests.Ai;

public class QuickIntentTests
{
    [Theory]
    [InlineData("y", IntentKind.Confirmation)]
    [InlineData("YES", IntentKind.Confirmation)]
    [InlineData(" sure ", IntentKind.Confirmation)]
    [InlineData("of course", IntentKind.Confirmation)]
    [InlineData("for sure", IntentKind.Confirmation)]
    [InlineData("yes please", IntentKind.Confirmation)]
    [InlineData("yse", IntentKind.Confirmation)]
    [InlineData("yeh", IntentKind.Confirmation)]
    [InlineData("no", IntentKind.Rejection)]
    [InlineData("nope", IntentKind.Rejection)]
    [InlineData("noo", IntentKind.Rejection)]
    [InlineData("no thanks", IntentKind.Rejection)]
    [InlineData("never mind", IntentKind.Rejection)]
    [InlineData("stop", IntentKind.Rejection)]               // regression: stop stays Rejection
    [InlineData("cancel", IntentKind.Cancel)]                // moved from Rejection — abort token
    [InlineData("CANCEL", IntentKind.Cancel)]
    [InlineData("end", IntentKind.Cancel)]
    [InlineData("bye", IntentKind.Cancel)]
    [InlineData("goodbye", IntentKind.Cancel)]
    [InlineData("exit", IntentKind.Cancel)]
    [InlineData("quit", IntentKind.Cancel)]
    [InlineData("leave", IntentKind.Cancel)]
    [InlineData("done", IntentKind.Cancel)]
    [InlineData("byee", IntentKind.Cancel)]                  // fuzzy
    [InlineData("hi", IntentKind.Greeting)]
    [InlineData("HI", IntentKind.Greeting)]
    [InlineData("hello", IntentKind.Greeting)]
    [InlineData("Hello", IntentKind.Greeting)]
    [InlineData("hey", IntentKind.Greeting)]
    [InlineData("hola", IntentKind.Greeting)]
    [InlineData("salam", IntentKind.Greeting)]
    [InlineData("salaam", IntentKind.Greeting)]
    [InlineData("howdy", IntentKind.Greeting)]
    [InlineData("good morning", IntentKind.Greeting)]
    [InlineData("good afternoon", IntentKind.Greeting)]
    [InlineData("good evening", IntentKind.Greeting)]
    [InlineData("hi there", IntentKind.Greeting)]
    [InlineData("hey there", IntentKind.Greeting)]
    [InlineData("how are you", IntentKind.Greeting)]
    [InlineData("NO THANKS", IntentKind.Rejection)]
    [InlineData("Of Course", IntentKind.Confirmation)]
    [InlineData("edit", IntentKind.Edit)]
    [InlineData(" EDIT ", IntentKind.Edit)]
    [InlineData("that's right", IntentKind.Confirmation)]
    [InlineData("That's right", IntentKind.Confirmation)]
    [InlineData("thats right", IntentKind.Confirmation)]                 // missing apostrophe
    [InlineData("that’s right", IntentKind.Confirmation)]           // curly apostrophe (mobile autocorrect)
    [InlineData("you're right", IntentKind.Confirmation)]
    [InlineData("you’re right", IntentKind.Confirmation)]           // curly apostrophe
    [InlineData("you got it", IntentKind.Confirmation)]
    [InlineData("got it", IntentKind.Confirmation)]
    [InlineData("sounds good", IntentKind.Confirmation)]
    [InlineData("sounds right", IntentKind.Confirmation)]
    [InlineData("that's it", IntentKind.Confirmation)]
    [InlineData("correct", IntentKind.Confirmation)]
    [InlineData("CORRECT", IntentKind.Confirmation)]
    [InlineData("exactly", IntentKind.Confirmation)]
    [InlineData("corect", IntentKind.Confirmation)]                      // fuzzy typo of "correct"
    [InlineData("exacly", IntentKind.Confirmation)]                      // fuzzy typo of "exactly"
    [InlineData("wrong", IntentKind.Rejection)]
    [InlineData("incorrect", IntentKind.Rejection)]
    [InlineData("not right", IntentKind.Rejection)]
    [InlineData("that's wrong", IntentKind.Rejection)]
    [InlineData("wong", IntentKind.Rejection)]                           // fuzzy typo of "wrong"
    // Affirmatives advertised after match presentation (post-paraphrase / authoritative CTA).
    [InlineData("proceed", IntentKind.Confirmation)]
    [InlineData("PROCEED", IntentKind.Confirmation)]
    [InlineData("Proceed", IntentKind.Confirmation)]
    [InlineData("continue", IntentKind.Confirmation)]
    [InlineData("Continue", IntentKind.Confirmation)]
    [InlineData("details", IntentKind.Confirmation)]
    [InlineData("detail", IntentKind.Confirmation)]
    [InlineData("Detail", IntentKind.Confirmation)]
    [InlineData("info", IntentKind.Confirmation)]
    [InlineData("share", IntentKind.Confirmation)]
    [InlineData("connect", IntentKind.Confirmation)]
    [InlineData("go", IntentKind.Confirmation)]
    [InlineData("go ahead", IntentKind.Confirmation)]
    [InlineData("go on", IntentKind.Confirmation)]
    [InlineData("more info", IntentKind.Confirmation)]
    [InlineData("more information", IntentKind.Confirmation)]
    [InlineData("more details", IntentKind.Confirmation)]
    [InlineData("tell me more", IntentKind.Confirmation)]
    [InlineData("send it", IntentKind.Confirmation)]
    [InlineData("share contact", IntentKind.Confirmation)]
    [InlineData("connect us", IntentKind.Confirmation)]
    [InlineData("connect me", IntentKind.Confirmation)]
    [InlineData("intro me", IntentKind.Confirmation)]
    [InlineData("want to proceed", IntentKind.Confirmation)]
    [InlineData("i want to proceed", IntentKind.Confirmation)]
    // Fuzzy typos for the longer affirmative tokens.
    [InlineData("procced", IntentKind.Confirmation)]                     // fuzzy typo of "proceed"
    [InlineData("detials", IntentKind.Confirmation)]                     // fuzzy typo of "details"
    [InlineData("conect", IntentKind.Confirmation)]                      // fuzzy typo of "connect"
    // Bare "new" → close active request and prompt fresh.
    [InlineData("new", IntentKind.NewRequest)]
    [InlineData("NEW", IntentKind.NewRequest)]
    [InlineData(" New ", IntentKind.NewRequest)]
    public void Detect_KnownInputs(string input, IntentKind expected)
        => Assert.Equal(expected, QuickIntent.Detect(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I need a plumber")]
    [InlineData("plumbing")]
    [InlineData("yo")]                                       // 2 chars, distance-1 to "no" — must NOT collide
    [InlineData("of course not")]
    [InlineData("yes but no")]
    // Adjacent English words that were potential fuzzy collisions for the new
    // Confirmation tokens. Short tokens "go" / "info" / "share" are literal-only
    // (excluded from fuzzy table) so these must NOT match Confirmation.
    [InlineData("got")]                                      // dist 1 from "go" — must stay null
    [InlineData("into")]                                     // dist 1 from "info" — must stay null
    [InlineData("shore")]                                    // dist 1 from "share" — must stay null
    [InlineData("shape")]                                    // dist 1 from "share" — must stay null
    public void Detect_ReturnsNullForUnrelated(string? input)
        => Assert.Null(QuickIntent.Detect(input));

    // DetectIntentHint covers the cases the LLM IntentSystem prompt historically
    // mis-classified — problem statements, utility outages, bare trade offers.

    [Theory]
    [InlineData("My door is broken")]
    [InlineData("my door is broken")]
    [InlineData("MY DOOR IS BROKEN")]
    [InlineData("my pipes are leaking")]
    [InlineData("my car won't start")]
    [InlineData("my laptop stopped working")]
    [InlineData("my ac isn't cooling")]
    [InlineData("our wifi is down")]
    [InlineData("my window is cracked")]
    [InlineData("my pipe burst")]
    [InlineData("my fridge needs fixing")]
    [InlineData("our roof has a leak")]
    [InlineData("no power in my house")]
    [InlineData("no water in the house")]
    [InlineData("lost electricity")]
    [InlineData("water everywhere in my kitchen")]
    [InlineData("burst pipe upstairs")]
    [InlineData("can someone help with my fridge")]
    [InlineData("anyone fix doors")]
    [InlineData("help me with the plumbing")]
    [InlineData("I need help")]
    [InlineData("urgent plumber needed")]
    public void DetectIntentHint_ReturnsServiceRequest(string input)
        => Assert.Equal(IntentKind.ServiceRequest, QuickIntent.DetectIntentHint(input));

    [Theory]
    [InlineData("I'm a plumber")]
    [InlineData("Im a plumber")]
    [InlineData("i am an electrician")]
    [InlineData("I'm a delivery guy")]
    [InlineData("doing plumbing today")]
    [InlineData("offering carpentry")]
    [InlineData("available for delivery")]
    [InlineData("open for car repair")]
    [InlineData("I do plumbing")]
    [InlineData("I fix cars")]
    [InlineData("I repair laptops")]
    // Expanded profession list — common roles users self-describe with.
    [InlineData("I am teacher")]
    [InlineData("I'm a teacher")]
    [InlineData("im a chef")]
    [InlineData("I am a nurse")]
    [InlineData("I'm a baker")]
    [InlineData("I am a designer")]
    // Profession-suffix catchall (-er/-or/-ist/-ician/-smith) for the long tail.
    [InlineData("I'm a locksmith")]
    [InlineData("I am a photographer")]
    [InlineData("I am a dentist")]
    [InlineData("I am a translator")]
    // Verb-anchored offers — service taxonomy is dynamic, noun can be anything.
    [InlineData("I offer tutorial")]
    [InlineData("I offer taxi")]
    [InlineData("I offer hauling")]
    [InlineData("I do hauling")]
    [InlineData("I do babysitting")]
    [InlineData("doing taxi")]
    [InlineData("offering lessons")]
    [InlineData("available for hauling")]
    [InlineData("open for tutoring")]
    // "no I am a teacher" — provider hint embedded after a leading rejection
    // must still be detected so the router can cross-flow switch.
    [InlineData("no I am a teacher")]
    [InlineData("no I'm a plumber")]
    public void DetectIntentHint_ReturnsProviderRegistration(string input)
        => Assert.Equal(IntentKind.ProviderRegistration, QuickIntent.DetectIntentHint(input));

    [Theory]
    [InlineData("next")]
    [InlineData("Next")]
    [InlineData("NEXT")]
    [InlineData("more")]
    [InlineData("more please")]
    [InlineData("show more")]
    [InlineData("another one")]
    [InlineData("other ones")]
    public void DetectIntentHint_ReturnsNextMatches(string input)
        => Assert.Equal(IntentKind.NextMatches, QuickIntent.DetectIntentHint(input));

    [Theory]
    [InlineData("increase")]
    [InlineData("Increase")]
    [InlineData("INCREASE")]
    [InlineData("wider")]
    [InlineData("widen")]
    [InlineData("widen the search")]
    [InlineData("expand")]
    [InlineData("broaden")]
    [InlineData("further")]
    public void DetectIntentHint_ReturnsIncreaseRange(string input)
        => Assert.Equal(IntentKind.IncreaseRange, QuickIntent.DetectIntentHint(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]                                      // handled by Detect, not DetectIntentHint
    [InlineData("no")]
    [InlineData("hello")]
    [InlineData("hi there")]
    [InlineData("door")]                                      // bare service noun — must remain ambiguous
    [InlineData("plumbing")]
    [InlineData("door work")]                                 // bare service noun + work — still ambiguous
    [InlineData("I need a plumber")]                          // covered by IntentSystem prompt, not the hint
    [InlineData("looking for a carpenter")]                   // covered by IntentSystem prompt, not the hint
    // Suffix catchall must NOT match adjectives or non-profession words.
    [InlineData("I am happy")]
    [InlineData("I am Bob")]
    [InlineData("I am tired")]
    [InlineData("I want to be rich")]                         // post-greeting noise — must stay ambiguous
    public void DetectIntentHint_ReturnsNullForAmbiguousOrUnrelated(string? input)
        => Assert.Null(QuickIntent.DetectIntentHint(input));

    [Theory]
    [InlineData("stop")]                       // literal fuzzy
    [InlineData("STOP")]
    [InlineData("stp")]                        // distance-1 typo
    [InlineData("stop asking me")]             // phrase
    [InlineData("stop asking")]
    [InlineData("don't ask")]
    [InlineData("dont ask")]
    [InlineData("do not ask")]
    [InlineData("leave me alone")]
    [InlineData("never again")]
    [InlineData("unsubscribe")]
    public void DetectStop_PositiveCases(string text) =>
        Assert.True(QuickIntent.DetectStop(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]                          // bare rejection, not stop
    [InlineData("yes please")]
    [InlineData("ok")]
    [InlineData("not yet")]                     // reschedule, not stop
    [InlineData(null)]
    public void DetectStop_NegativeCases(string? text) =>
        Assert.False(QuickIntent.DetectStop(text));

    [Theory]
    [InlineData("still looking")]
    [InlineData("not yet")]
    [InlineData("not now")]
    [InlineData("give me time")]
    [InlineData("give me some time")]
    [InlineData("ask me later")]
    [InlineData("ask me tomorrow")]
    [InlineData("check back tomorrow")]
    [InlineData("in a bit")]
    [InlineData("in a moment")]
    [InlineData("need more time")]
    [InlineData("later")]                       // bare token
    [InlineData("tomorrow")]
    public void DetectReschedule_PositiveCases(string text) =>
        Assert.True(QuickIntent.DetectReschedule(text));

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("stop")]
    [InlineData(null)]
    public void DetectReschedule_NegativeCases(string? text) =>
        Assert.False(QuickIntent.DetectReschedule(text));
}
