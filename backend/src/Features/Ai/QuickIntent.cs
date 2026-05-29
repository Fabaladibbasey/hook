using System.Text.RegularExpressions;
using Hook.Features.Ai.Models;

namespace Hook.Features.Ai;

public static class QuickIntent
{
    private static readonly RegexOptions RxOpts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

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
        @"\b(i'?m|i am)\s+(a\s+|an\s+)?(plumber|electrician|mechanic|carpenter|tutor|painter|cleaner|driver|barber|tailor|welder|mason|builder|contractor|handyman|handywoman|teacher|professor|instructor|trainer|coach|nurse|chef|cook|gardener|baker|fisherman|babysitter|photographer|designer|writer|dancer|singer|musician|farmer|hairdresser|stylist|technician|locksmith|delivery\s+(guy|man|woman|person|driver|rider))\b",
        RxOpts);

    // Profession-suffix catchall: "I'm a/an X" where X ends in a common
    // occupational suffix. \w{3,} prefix avoids "I am her/over" matches and
    // covers the long tail (translator, locksmith, dancer, designer) without
    // enumerating every noun.
    private static readonly Regex IAmRoleSuffixRx = new(
        @"\b(i'?m|i am)\s+(a\s+|an\s+)?(\w+\s+){0,2}\w{3,}(er|or|ist|ician|smith)\b",
        RxOpts);

    // "doing/offering/providing/available for/open for X" — verb is the strong
    // provider-intent signal in this funnel domain. Service taxonomy is dynamic
    // (SlugResolver canonicalises any noun), so we don't enumerate nouns.
    private static readonly Regex BareOfferRx = new(
        @"\b(doing|offering|providing|available\s+for|open\s+for|taking\s+on)\b\s+\w{3,30}",
        RxOpts);

    // "I do/offer/provide/fix/repair/run/handle X" — same verb-anchored pattern.
    private static readonly Regex IDoTradeRx = new(
        @"\b(i)\s+(do|offer|provide|fix|repair|run|handle)\b\s+\w{3,30}",
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
        (IAmRoleSuffixRx,           IntentKind.ProviderRegistration),
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

    private static readonly string[] ConfirmTokens =
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
    private static readonly string[] RejectTokens = ["n", "no", "nope", "nah", "stop", "wrong", "incorrect"];
    private static readonly string[] CancelTokens =
        ["cancel", "end", "bye", "goodbye", "exit", "quit", "leave", "done"];

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
        ("that's right",   IntentKind.Confirmation),
        ("thats right",    IntentKind.Confirmation),
        ("that's correct", IntentKind.Confirmation),
        ("thats correct",  IntentKind.Confirmation),
        ("you're right",   IntentKind.Confirmation),
        ("youre right",    IntentKind.Confirmation),
        ("you got it",    IntentKind.Confirmation),
        ("got it",        IntentKind.Confirmation),
        ("any tip",       IntentKind.TipRequest),
        ("any tips",      IntentKind.TipRequest),
        ("got a tip",     IntentKind.TipRequest),
        ("got tips",      IntentKind.TipRequest),
        ("got any tips",  IntentKind.TipRequest),
        ("any advice",    IntentKind.TipRequest),
        ("got advice",    IntentKind.TipRequest),
        ("give me a tip", IntentKind.TipRequest),
        ("tip please",    IntentKind.TipRequest),
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
            .Replace('”', '"')
            .TrimEnd('.', '!', '?', ',');
        if (string.IsNullOrEmpty(s)) return null;

        // 1. Literal exact match (fast path; preserves all existing behaviour)
        var literal = s switch
        {
            "y" or "yes" or "yeah" or "yep" or "yup" or "ok" or "okay" or "sure" or "confirm" or "correct" or "exactly"
                or "proceed" or "continue" or "go" or "details" or "detail" or "info" or "share" or "connect"
                => IntentKind.Confirmation,
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

    // Step1 feedback-specific hints. Kept here so QuickIntent stays the one
    // place deterministic text classification lives. Returned as bool because
    // the caller already owns the Step1ReplyIntent record.

    private static readonly string[] StopTokens =
    [
        "stop", "never", "unsubscribe", "leavemealone", "dontask", "donotask"
    ];

    private static readonly string[] RescheduleTokens =
    [
        "later", "tomorrow", "tonight", "nextweek", "soon",
        "stilllooking", "notyet", "givemetime", "checkback"
    ];

    private static readonly Regex StopPhraseRx = new(
        @"\b(stop\s+asking|don'?t\s+ask|leave\s+me\s+alone|do\s+not\s+ask|never\s+again|unsubscribe)\b",
        RxOpts);

    private static readonly Regex ReschedulePhraseRx = new(
        @"\b(still\s+looking|not\s+yet|give\s+me\s+(time|some\s+time|a\s+(min|moment|sec))|"
        + @"check\s+back|come\s+back|ask\s+(me\s+)?(later|tomorrow|next\s+week)|"
        + @"(try|ping|hit\s+me)\s+(again\s+)?(later|tomorrow|next\s+week)|"
        + @"in\s+(a\s+)?(bit|while|moment)|not\s+(right\s+)?now|need\s+(more\s+)?time)\b",
        RxOpts);

    // Lowercase + trim + strip trailing punctuation. Single allocation site reused
    // across Step1/Step2 deterministic detectors so a multi-call inbound (e.g. Step2
    // → DetectInProgress → DetectReschedule) does not re-normalise the same text.
    // Trim set MUST match QuickIntent.Detect's trailing-punctuation strip so
    // detectors that route through Normalize see the same canonical form.
    internal static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ',');

    /// <summary>True when the text reads as "stop asking me about this".</summary>
    public static bool DetectStop(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Normalize(text);
        if (StopPhraseRx.IsMatch(s)) return true;

        // Fuzzy single-token: collapse internal whitespace so "leave me alone" still
        // checks as a single 12-char token against "leavemealone".
        var compact = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        return compact.Length >= 3 && FuzzyMatch.MatchesAny(compact, StopTokens, 1);
    }

    /// <summary>True when the text asks for a later check-in. Eta resolution is the caller's job.</summary>
    public static bool DetectReschedule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Normalize(text);
        if (ReschedulePhraseRx.IsMatch(s)) return true;
        var compact = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        return compact.Length >= 4 && FuzzyMatch.MatchesAny(compact, RescheduleTokens, 1);
    }

    // Step2 InProgress hints: "still working", "almost done", "halfway", "not
    // finished" etc. Kept separate from DetectReschedule so Step1 callers do not
    // gain Step2-specific matches. DetectReschedule covers "not yet"/"later"/
    // "tomorrow"/"in a bit" already — those imply the job is not finished but
    // will be, semantically equivalent to InProgress for Step2.
    private static readonly string[] InProgressTokens =
    [
        "stillworking", "workingonit", "stilldoing", "almostdone", "almostfinished",
        "halfway", "halfwaydone", "ongoing", "doingitnow", "notdone", "notfinished",
        "notcomplete"
    ];

    private static readonly Regex InProgressPhraseRx = new(
        @"\b(still\s+working|working\s+on\s+(it|that)|still\s+doing|almost\s+(done|finished)|"
        + @"halfway(\s+done)?|in\s+the\s+middle\s+(of\s+it)?|ongoing|doing\s+it\s+now|"
        + @"not\s+(done|finished|complete)|"
        + @"in\s+\d+\s+(hour|hr|minute|min|day)s?)\b",
        RxOpts);

    /// <summary>True when the text reads as "the job is underway / not finished yet".
    /// Short-circuits through <see cref="DetectReschedule"/> internally so callers
    /// need only invoke this detector for full Step2 coverage.</summary>
    public static bool DetectInProgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (DetectReschedule(text)) return true;
        var s = Normalize(text);
        if (InProgressPhraseRx.IsMatch(s)) return true;
        var compact = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        return compact.Length >= 4 && FuzzyMatch.MatchesAny(compact, InProgressTokens, 1);
    }

    // Interrogative leaders — the cheap deterministic gate the cold-path Q&A
    // short-circuit and the mid-flow orchestrators check before falling back to
    // the LLM. Trailing '?' is the dominant signal; the regex covers bare
    // English question lead-ins. "is/are/will/who" deliberately omitted —
    // they collide with service-request patterns ("is my pipe leaking",
    // "are you free", "will it cost", "who can help") which the hint detector
    // already classifies as ServiceRequest.
    private static readonly Regex PlatformQuestionRx = new(
        @"^\s*(what|how|why|when|where|which|can|could|does|do|should)\b",
        RxOpts);

    /// <summary>True when the text reads as an English question — either ends with '?' or
    /// leads with an interrogative pronoun/auxiliary, AND no deterministic service-request /
    /// provider-registration hint applies (those hints win — e.g. "is my pipe leaking?"
    /// reads as ServiceRequest, not a platform question). Used as a deterministic prefilter
    /// before publishing <c>AnswerPlatformQuestionCommand</c>.</summary>
    public static bool LooksLikePlatformQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.Length < 3) return false;
        if (!s.EndsWith('?') && !PlatformQuestionRx.IsMatch(s)) return false;
        return DetectIntentHint(text) is null;
    }

    private static readonly Regex RelativeEtaRx = new(
        @"\bin\s+(\d{1,3})\s+(hour|hr|minute|min|day)s?\b",
        RxOpts);

    /// <summary>Extracts a relative-duration ETA ("in 3 hours", "in 30 minutes") deterministically
    /// so the layered Step2 parser does not need to round-trip to Ollama for the common case.
    /// Returns null on no match, non-positive duration, or absolute forms ("tomorrow at 5pm").</summary>
    public static DateTimeOffset? TryExtractRelativeEta(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = RelativeEtaRx.Match(text);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "hour" or "hr" => now.AddHours(n),
            "minute" or "min" => now.AddMinutes(n),
            "day" => now.AddDays(n),
            _ => null
        };
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

        // Bare-keyword shortcuts: single words the bot advertises ("REQUEST a service",
        // "Reply HIRE or OFFER", "TIP for usage guidance"). Deterministic so the LLM
        // never sees them. Lifted above the length floor so 3-char "tip" matches.
        var lower = s.ToLowerInvariant().TrimEnd('.', '!', '?');
        var bareIntent = lower switch
        {
            "request" or "hire" => (IntentKind?)IntentKind.ServiceRequest,
            "register" or "offer" => IntentKind.ProviderRegistration,
            "tip" or "tips" or "advice" => IntentKind.TipRequest,
            _ => null
        };
        if (bareIntent is not null) return bareIntent;

        // Short utterances handled by Detect (yes/no/edit/etc) shouldn't be re-classified
        // against the regex HintRules.
        if (s.Length < 4) return null;

        foreach (var (rx, intent) in HintRules)
            if (rx.IsMatch(s)) return intent;

        return null;
    }
}
