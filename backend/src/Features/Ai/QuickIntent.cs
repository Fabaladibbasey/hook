using System.Text.RegularExpressions;
using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public static class QuickIntent
{
    private static readonly RegexOptions RxOpts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // ServiceRequest hints — the user is reporting a problem they have.

    // "my/our X (is|are|was|won't|isn't|stopped|keeps) ... broken/leaking/dead/..."
    private static readonly Regex PossessiveProblemRx = new(
        @"\b(my|our)\b.{1,40}?\b(is|are|isn'?t|aren'?t|was|were|won'?t|wont|stopped|keeps?|keep|got|getting|has|have|had)\b.{0,30}?\b(broken|broke|leaking|leak|leaks|down|dead|stuck|jammed|cracked|smoking|smelling|flooded|frozen|stopped|fixed|working|cooling|heating|charging|connecting|booting|spinning|loading|running|busted|burst|burnt|burned|spoiled|damaged)\b",
        RxOpts);

    // "my X (isn't|won't|doesn't|can't) Y" — generic negative-state pattern catches things
    // like "my ac isn't cooling" or "my phone won't charge" without a vocab entry per Y.
    private static readonly Regex PossessiveNegativeStateRx = new(
        @"\b(my|our)\b.{1,40}?\b(isn'?t|aren'?t|won'?t|wont|doesn'?t|don'?t|can'?t|cannot|wasn'?t|weren'?t|hasn'?t|haven'?t|didn'?t)\b\s+\w+",
        RxOpts);

    // "my X needs / wants" + repair/fixing/help, or "my X has a problem"
    private static readonly Regex PossessiveNeedsRx = new(
        @"\b(my|our)\b.{1,40}?\b(needs?|wants?|has|have)\b.{0,20}?\b(fix|fixing|repair|repaired|repairing|help|problem|issue|trouble)\b",
        RxOpts);

    // "my X burst / broke / leaked / flooded / crashed" — past-tense state verb where
    // the verb itself is the problem signal (no separate "is/was" linking verb).
    private static readonly Regex PossessiveStateVerbRx = new(
        @"\b(my|our)\b\s+(\w+\s+){0,3}\b(burst|broke|leaked|leaks|flooded|cracked|jammed|stuck|busted|crashed|froze|died|exploded|smoking|leaking)\b",
        RxOpts);

    // "no power / no water / lost electricity / no internet / no signal / no gas / no heat"
    private static readonly Regex UtilityOutageRx = new(
        @"\b(no|lost|without|out of)\s+(power|water|electricity|internet|wifi|signal|gas|heat|hot water|light|lights)\b",
        RxOpts);

    // "X everywhere" cries
    private static readonly Regex DisasterRx = new(
        @"\b(water|gas|smoke|fire|sewage|sewer)\s+everywhere\b|\b(flood|flooding|fire|burst pipe|gas leak|electrical fire)\b",
        RxOpts);

    // imperative / asking for help: "can someone help with X", "anyone fix X", "I need help"
    private static readonly Regex AskForHelpRx = new(
        @"\b(can\s+(someone|anyone|anybody)|need\s+help\s+with|help\s+me\s+with|i\s+need\s+help|please\s+help|anyone\s+(fix|repair|help))\b",
        RxOpts);

    // "emergency / urgent" + a service-domain word (note: \w* on stems so "plumber",
    // "electrician" etc match without listing every variant).
    private static readonly Regex EmergencyRx = new(
        @"\b(emergency|urgent|asap|right now)\b.{0,40}?\b(plumb\w*|electric\w*|mechanic\w*|carpent\w*|car|cars|fridge|ac|stove|oven|washer|door|window|roof|leak\w*|water|power|gas|fire|broken)\b",
        RxOpts);

    // ProviderRegistration hints — the user is offering a skill/trade.

    // "I'm a plumber/electrician/mechanic/carpenter/tutor/painter/cleaner/driver/..."
    private static readonly Regex IAmTradeRx = new(
        @"\b(i'?m|i am)\s+(a\s+|an\s+)?(plumber|electrician|mechanic|carpenter|tutor|painter|cleaner|driver|barber|tailor|welder|mason|builder|contractor|handyman|handywoman|delivery\s+(guy|man|woman|person|driver|rider))\b",
        RxOpts);

    // "doing/offering/providing/available for/open for X" where X is a known service noun
    private static readonly Regex BareOfferRx = new(
        @"\b(doing|offering|providing|available\s+for|open\s+for|taking\s+on)\b\s+(plumbing|carpentry|delivery|deliveries|cleaning|tutoring|painting|electrical|electrical\s+work|car\s+repair|auto\s+repair|mechanic\s+work|garage\s+work|door\s+repair|appliance\s+repair|computer\s+repair|laptop\s+repair|phone\s+repair)\b",
        RxOpts);

    // "I do/offer/provide/fix/repair X" — covered by LLM prompt too, but cheap to short-circuit.
    private static readonly Regex IDoTradeRx = new(
        @"\b(i)\s+(do|offer|provide|fix|repair|run|handle)\b\s+(plumbing|carpentry|delivery|deliveries|cleaning|tutoring|painting|electrical|car\s+repair|auto\s+repair|mechanic|garage|door\s+repair|appliance\s+repair|computer\s+repair|laptop\s+repair|phone\s+repair|cars|doors|windows|roofs|pipes|wiring|appliances|computers|laptops|phones)\b",
        RxOpts);

    // Pagination hints — short tokens that the LLM tends to mis-classify as ServiceRequest.
    private static readonly Regex NextMatchesRx = new(
        @"^\s*(next|more|another\s+one|other\s+ones?|show\s+more)\b",
        RxOpts);

    private static readonly Regex IncreaseRangeRx = new(
        @"^\s*(increase|wider|widen|expand|broaden|further)\b",
        RxOpts);

    private static readonly (Regex Rx, IntentKind Intent)[] HintRules =
    [
        // Pagination first — short literal tokens overlap with nothing else.
        (NextMatchesRx,             IntentKind.NextMatches),
        (IncreaseRangeRx,           IntentKind.IncreaseRange),
        // Provider hints — "I'm a plumber" must beat any incidental possessive match.
        (IAmTradeRx,                IntentKind.ProviderRegistration),
        (BareOfferRx,               IntentKind.ProviderRegistration),
        (IDoTradeRx,                IntentKind.ProviderRegistration),
        // Then problem-statement hints.
        (PossessiveProblemRx,       IntentKind.ServiceRequest),
        (PossessiveNegativeStateRx, IntentKind.ServiceRequest),
        (PossessiveStateVerbRx,     IntentKind.ServiceRequest),
        (PossessiveNeedsRx,         IntentKind.ServiceRequest),
        (UtilityOutageRx,           IntentKind.ServiceRequest),
        (DisasterRx,                IntentKind.ServiceRequest),
        (AskForHelpRx,              IntentKind.ServiceRequest),
        (EmergencyRx,               IntentKind.ServiceRequest),
    ];

    private static readonly string?[] ConfirmTokens =
    [
        "y", "yes", "yeah", "yep", "yup", "ok", "okay", "sure", "confirm", "correct", "exactly",
        // Affirmatives the bot may advertise after presenting matches ("Reply 1 to
        // connect…", "Would you like more information or want to proceed?"). Keeping
        // them deterministic short-circuits the LLM, which mis-classifies short
        // tokens — especially for users who are also listed providers.
        // Short tokens "go" / "info" / "share" are intentionally LITERAL-only
        // (handled in the switch below) so distance-1 fuzzy doesn't collide with
        // adjacent English words like "got" / "into" / "shore".
        "proceed", "continue", "details", "detail", "connect"
    ];
    private static readonly string?[] RejectTokens = ["n", "no", "nope", "nah", "stop", "wrong", "incorrect"];
    private static readonly string?[] CancelTokens = ["cancel", "end", "bye", "goodbye", "exit", "quit", "leave", "done"];

    private static readonly (string Phrase, IntentKind Intent)[] Phrases =
    [
        ("of course",     IntentKind.Confirmation),
        ("for sure",      IntentKind.Confirmation),
        ("sure thing",    IntentKind.Confirmation),
        ("yes please",    IntentKind.Confirmation),
        ("go ahead",      IntentKind.Confirmation),
        ("go on",         IntentKind.Confirmation),
        ("more info",     IntentKind.Confirmation),
        ("more information", IntentKind.Confirmation),
        ("more details",  IntentKind.Confirmation),
        ("more detail",   IntentKind.Confirmation),
        ("tell me more",  IntentKind.Confirmation),
        ("send it",       IntentKind.Confirmation),
        ("send contact",  IntentKind.Confirmation),
        ("share contact", IntentKind.Confirmation),
        ("share it",      IntentKind.Confirmation),
        ("connect us",    IntentKind.Confirmation),
        ("connect me",    IntentKind.Confirmation),
        ("intro me",      IntentKind.Confirmation),
        ("i want to proceed", IntentKind.Confirmation),
        ("want to proceed",   IntentKind.Confirmation),
        ("i want details",    IntentKind.Confirmation),
        ("want details",      IntentKind.Confirmation),
        ("that's right",  IntentKind.Confirmation),
        ("thats right",   IntentKind.Confirmation),
        ("you're right",  IntentKind.Confirmation),
        ("youre right",   IntentKind.Confirmation),
        ("you got it",    IntentKind.Confirmation),
        ("got it",        IntentKind.Confirmation),
        ("sounds good",   IntentKind.Confirmation),
        ("sounds right",  IntentKind.Confirmation),
        ("that's it",     IntentKind.Confirmation),
        ("thats it",      IntentKind.Confirmation),
        ("no thanks",     IntentKind.Rejection),
        ("no way",        IntentKind.Rejection),
        ("hell no",       IntentKind.Rejection),
        ("never mind",    IntentKind.Rejection),
        ("not right",     IntentKind.Rejection),
        ("that's wrong",  IntentKind.Rejection),
        ("thats wrong",   IntentKind.Rejection),
        ("good morning",   IntentKind.Greeting),
        ("good afternoon", IntentKind.Greeting),
        ("good evening",   IntentKind.Greeting),
        ("good day",       IntentKind.Greeting),
        ("hi there",       IntentKind.Greeting),
        ("hey there",      IntentKind.Greeting),
        ("how are you",    IntentKind.Greeting),
    ];

    public static IntentKind? Detect(string? text)
    {
        // Normalise curly apostrophes (U+2018 / U+2019) and quotes (U+201C / U+201D) to ASCII
        // so mobile autocorrect's "that's" still matches the canonical "that's" phrase.
        var s = text?.Trim().ToLowerInvariant()
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('“', '"')
            .Replace('”', '"');
        if (string.IsNullOrEmpty(s)) return null;

        // 1. Literal exact match (fast path; preserves all existing behaviour)
        var literal = s switch
        {
            "y" or "yes" or "yeah" or "yep" or "yup" or "ok" or "okay" or "sure" or "confirm" or "correct" or "exactly"
                or "proceed" or "continue" or "go" or "details" or "detail" or "info" or "share" or "connect" => IntentKind.Confirmation,
            "n" or "no" or "nope" or "nah" or "stop" or "wrong" or "incorrect" => IntentKind.Rejection,
            "cancel" or "end" or "bye" or "goodbye" or "exit" or "quit" or "leave" or "done" => IntentKind.Cancel,
            "edit" => IntentKind.Edit,
            "new" => IntentKind.NewRequest,
            // "yo" intentionally excluded — too close to "no" for fuzzy match safety.
            "hi" or "hello" or "hey" or "hola" or "salam" or "salaam" or "howdy" => IntentKind.Greeting,
            _ => (IntentKind?)null
        };
        if (literal is not null) return literal;

        // 2. Multi-word phrase contains-check
        foreach (var (phrase, intent) in Phrases)
            if (s == phrase) return intent;

        // 3. Fuzzy single-token (distance-1) for plausible typos (length >= 3 to avoid noise)
        if (s.Length >= 3)
        {
            if (FuzzyMatch.MatchesAny(s, ConfirmTokens, 1)) return IntentKind.Confirmation;
            if (FuzzyMatch.MatchesAny(s, RejectTokens, 1)) return IntentKind.Rejection;
            if (FuzzyMatch.MatchesAny(s, CancelTokens, 1)) return IntentKind.Cancel;
        }
        return null;
    }

    /// <summary>
    /// Deterministic hint for ServiceRequest vs ProviderRegistration based on regex patterns
    /// that the LLM IntentSystem prompt can miss (problem statements like "my door is broken",
    /// utility outages, bare trade offers). Returns null when no pattern matches — the caller
    /// should then fall back to the LLM classifier.
    /// </summary>
    public static IntentKind? DetectIntentHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        // Short utterances handled by Detect (yes/no/edit/etc) shouldn't be re-classified here.
        if (s.Length < 4) return null;

        // Bare-keyword shortcuts: single words the bot advertises ("REQUEST a service",
        // "Reply HIRE or OFFER"). Deterministic so the LLM never sees them.
        var lower = s.ToLowerInvariant().TrimEnd('.', '!', '?');
        var bareIntent = lower switch
        {
            "request" or "hire" => (IntentKind?)IntentKind.ServiceRequest,
            "register" or "offer" => IntentKind.ProviderRegistration,
            _ => null
        };
        if (bareIntent is not null) return bareIntent;

        foreach (var (rx, intent) in HintRules)
            if (rx.IsMatch(s)) return intent;

        return null;
    }
}
