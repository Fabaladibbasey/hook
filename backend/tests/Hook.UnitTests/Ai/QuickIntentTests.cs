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
    public void DetectIntentHint_ReturnsNullForAmbiguousOrUnrelated(string? input)
        => Assert.Null(QuickIntent.DetectIntentHint(input));
}
