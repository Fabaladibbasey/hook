using System.Collections.Frozen;
using Hook.Features.Feedback;
using Hook.Features.Feedback.AggregateStats;
using Hook.Features.Observability;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Hook.Features.Ai.PlatformQa;

// One publisher for AnswerPlatformQuestionCommand. Centralises the
// scrub-then-locale-sanitize-then-publish pair the router and orchestrators
// were each doing inline with three different locale defaults.
//
// Cold-path callers send an immediate ack so the user does not stare at a
// silent WhatsApp window during the 60-150s Ollama window. Mid-flow callers
// own their own re-prompt (which IS the ack) so they call the dispatcher with
// no ack here.
//
// Identity-question shortcut: normalized identity phrases in any supported
// language bypass Ollama + the dedup gate + the cold ack and send a canned
// per-locale identity reply directly. The phrase carries its own locale so the
// router-pinned "en" default (cold deterministic path) does not force English
// replies on French / Spanish / Portuguese / Arabic / Wolof speakers.
// Other phrases still go through the LLM path with the hardened prompt.
public sealed class PlatformQaDispatcher(
    IMessageBus bus,
    IOptions<FeedbackOptions> feedbackOptions,
    IPlatformAnswerDedupGate dedup)
{
    private static readonly FrozenDictionary<string, string> IdentityPhrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // English
            ["what is this"] = "en",
            ["what's this"] = "en",
            ["whats this"] = "en",
            ["what is hook"] = "en",
            ["what's hook"] = "en",
            ["whats hook"] = "en",
            ["what are you"] = "en",
            ["what're you"] = "en",
            ["what r u"] = "en",
            ["who are you"] = "en",
            ["who r u"] = "en",
            ["what is your name"] = "en",
            ["what's your name"] = "en",
            ["whats your name"] = "en",
            ["your name"] = "en",
            // French
            ["qui es tu"] = "fr",
            ["qui es-tu"] = "fr",
            ["qui êtes vous"] = "fr",
            ["qui êtes-vous"] = "fr",
            ["c'est quoi"] = "fr",
            ["c'est quoi ça"] = "fr",
            ["c'est quoi hook"] = "fr",
            ["quel est ton nom"] = "fr",
            // Arabic
            ["من انت"] = "ar",
            ["ما هذا"] = "ar",
            ["ما اسمك"] = "ar",
            // Wolof
            ["yan nga tudd"] = "wo",
            ["loolu lan la"] = "wo",
            // Spanish
            ["que es esto"] = "es",
            ["qué es esto"] = "es",
            ["quien eres"] = "es",
            ["quién eres"] = "es",
            ["como te llamas"] = "es",
            ["cómo te llamas"] = "es",
            // Portuguese
            ["o que e isto"] = "pt",
            ["o que é isto"] = "pt",
            ["quem es voce"] = "pt",
            ["quem és você"] = "pt",
            ["qual e o seu nome"] = "pt",
            ["qual é o seu nome"] = "pt",
            // Fula (Pulaar). TODO: native-speaker review of phrasing + reply text.
            ["ko honɗun"] = "ff",
            ["ko honɗun woni ɗoo"] = "ff",
            // Mandinka. TODO: native-speaker review of phrasing + reply text.
            ["muna le ñin"] = "mnk",
            ["i too le"] = "mnk",
        }.ToFrozenDictionary();

    private const string IdentityReplyEn =
        "I'm Hook — a WhatsApp bot that connects you with nearby service providers, " +
        "or lists you as a provider so clients can find you. Reply REQUEST if you need " +
        "a service, or REGISTER to offer one. The platform is free during launch.";

    private static readonly FrozenDictionary<string, string> LocalisedIdentityReplies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fr"] =
                "Je suis Hook — un bot WhatsApp qui vous met en relation avec des prestataires " +
                "de services à proximité, ou vous inscrit comme prestataire pour que des clients " +
                "vous trouvent. Répondez REQUEST si vous cherchez un service, ou REGISTER pour en " +
                "proposer un. La plateforme est gratuite pendant le lancement.",
            ["ar"] =
                "أنا Hook — روبوت واتساب يربطك بمزودي الخدمات القريبين، أو يدرجك كمزود ليتمكن " +
                "العملاء من العثور عليك. أرسل REQUEST إذا كنت تحتاج خدمة، أو REGISTER لتقديم " +
                "خدمة. المنصة مجانية خلال فترة الإطلاق.",
            ["wo"] =
                "Maa di Hook — bot bu WhatsApp bu lay jokkale ak ñi koy joxe service ci sa wàll, " +
                "walla bu lay bind ndax sa kiliyaan yi gis la. Bind REQUEST su nga soxla service, " +
                "walla REGISTER ngir joxe benn. Plateforme bi neexul dara nag bi nu di tàmbali.",
            ["es"] =
                "Soy Hook — un bot de WhatsApp que te conecta con prestadores de servicios " +
                "cercanos, o te lista como prestador para que clientes te encuentren. Responde " +
                "REQUEST si necesitas un servicio, o REGISTER para ofrecer uno. La plataforma " +
                "es gratuita durante el lanzamiento.",
            ["pt"] =
                "Sou o Hook — um bot do WhatsApp que te conecta com prestadores de serviços " +
                "próximos, ou te lista como prestador para que os clientes te encontrem. " +
                "Responde REQUEST se precisas de um serviço, ou REGISTER para oferecer um. " +
                "A plataforma é gratuita durante o lançamento.",
            // TODO: native-speaker translations for ff / mnk. English fallback for now.
            ["ff"] = IdentityReplyEn,
            ["mnk"] = IdentityReplyEn,
        }.ToFrozenDictionary();

    public async ValueTask DispatchColdAsync(
        PhoneNumber to, string text, string locale, string contextHint, CancellationToken ct = default)
    {
        if (await TryDispatchIdentityAsync(to, text, locale, ct))
            return;

        await bus.PublishAsync(new SendWhatsAppTextCommand(to, "Got your message — one sec…"));
        await PublishAsync(to, text, locale, contextHint);
    }

    public async ValueTask DispatchMidFlowAsync(
        PhoneNumber to, string text, string locale, string contextHint, CancellationToken ct = default)
    {
        if (await TryDispatchIdentityAsync(to, text, locale, ct))
            return;

        await PublishAsync(to, text, locale, contextHint);
    }

    private async ValueTask<bool> TryDispatchIdentityAsync(
        PhoneNumber to, string text, string locale, CancellationToken ct)
    {
        var canonical = Normalize(text);
        if (!IdentityPhrases.TryGetValue(canonical, out var phraseLocale))
            return false;

        // Cold-deterministic callers pin locale="en" before any language signal
        // has been detected, so the phrase-detected locale wins for the empty /
        // "en" / "en-*" defaults. Any other caller-supplied locale (from the LLM
        // classifier path) wins because the classifier had real text signal.
        var resolved = string.IsNullOrEmpty(locale)
                    || string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase)
                    || locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            ? phraseLocale : locale;
        var safe = LocaleValidator.Sanitize(resolved);

        // Dedup gate: identity shortcut is no cheaper than a real Ollama hit
        // from the user's perspective (still a WhatsApp message Meta charges
        // for). Keyed by canonical phrase so "who are you" vs "what is this"
        // produce distinct entries.
        var hash = unchecked((long)System.IO.Hashing.XxHash64.HashToUInt64(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
        if (!await dedup.TryClaimAsync(to.Value, hash, ct))
        {
            HookMetrics.PlatformQaIdentityShortcut.Add(1,
                new KeyValuePair<string, object?>("locale", safe),
                new KeyValuePair<string, object?>("outcome", "dedup-suppressed"));
            return true;
        }

        var reply = IdentityReplyFor(safe);
        HookMetrics.PlatformQaIdentityShortcut.Add(1,
            new KeyValuePair<string, object?>("locale", safe),
            new KeyValuePair<string, object?>("outcome", "sent"));
        await bus.PublishAsync(new SendWhatsAppTextCommand(to, reply));
        return true;
    }

    internal static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Canonicalise mobile-keyboard punctuation: iOS / Android autocorrect
        // emits U+2019 curly apostrophe, but the IdentityPhrases keys are ASCII '.
        // Zero-width space U+200B can sneak in through copy-paste; collapse to
        // a regular space so the whitespace split still folds it.
        var canonical = text
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('​', ' ');
        // Strip ASCII '?' and Arabic question mark (U+061F) so Arabic / Wolof /
        // Fula phrases ending in '؟' still match the canonical entries.
        var lowered = canonical.Trim().ToLowerInvariant().TrimEnd('?', '؟').TrimEnd();
        return string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string IdentityReplyFor(string locale) =>
        LocalisedString.For(locale, LocalisedIdentityReplies, IdentityReplyEn);

    internal static string IdentityReplyForTest(string locale) => IdentityReplyFor(locale);

    private ValueTask PublishAsync(PhoneNumber to, string text, string locale, string contextHint)
    {
        var scrubbed = FeedbackResponseService.ScrubForOutbox(
            text, feedbackOptions.Value.OutboxTextMaxChars);
        var safe = LocaleValidator.Sanitize(locale);
        return bus.PublishAsync(new AnswerPlatformQuestionCommand(to, scrubbed, safe, contextHint));
    }
}
