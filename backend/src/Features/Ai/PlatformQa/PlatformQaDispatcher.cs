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
// Identity-question shortcut: exact English identity phrases ("what is this",
// "who are you", ...) bypass Ollama + the dedup gate + the cold ack and send
// a canned per-locale identity reply directly. Other phrases (and non-English
// identity questions) still go through the LLM path with the hardened prompt.
public sealed class PlatformQaDispatcher(
    IMessageBus bus,
    IOptions<FeedbackOptions> feedbackOptions)
{
    private static readonly FrozenSet<string> IdentityPhrases =
        new[]
        {
            "what is this",
            "what's this",
            "whats this",
            "what is hook",
            "what's hook",
            "whats hook",
            "what are you",
            "what're you",
            "what r u",
            "who are you",
            "who r u",
            "what is your name",
            "what's your name",
            "whats your name",
            "your name",
        }.ToFrozenSet(StringComparer.Ordinal);

    private const string IdentityReplyEn =
        "I'm Hook — a WhatsApp bot that connects you with nearby service providers, " +
        "or lists you as a provider so clients can find you. Reply REQUEST if you need " +
        "a service, or REGISTER to offer one. The platform is free during launch.";

    private static readonly FrozenDictionary<string, string> LocalisedIdentityReplies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = IdentityReplyEn,
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
        }.ToFrozenDictionary();

    public async ValueTask DispatchColdAsync(
        PhoneNumber to, string text, string locale, string contextHint)
    {
        if (await TryDispatchIdentityAsync(to, text, locale))
            return;

        await bus.PublishAsync(new SendWhatsAppTextCommand(to, "Got your message — one sec…"));
        await PublishAsync(to, text, locale, contextHint);
    }

    public async ValueTask DispatchMidFlowAsync(
        PhoneNumber to, string text, string locale, string contextHint)
    {
        if (await TryDispatchIdentityAsync(to, text, locale))
            return;

        await PublishAsync(to, text, locale, contextHint);
    }

    private async ValueTask<bool> TryDispatchIdentityAsync(PhoneNumber to, string text, string locale)
    {
        if (!IdentityPhrases.Contains(Normalize(text)))
            return false;

        var safe = LocaleValidator.Sanitize(locale);
        var reply = IdentityReplyFor(safe);
        HookMetrics.PlatformQaIdentityShortcut.Add(1,
            new KeyValuePair<string, object?>("locale", safe));
        await bus.PublishAsync(new SendWhatsAppTextCommand(to, reply));
        return true;
    }

    internal static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lowered = text.Trim().ToLowerInvariant().TrimEnd('?').TrimEnd();
        return string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string IdentityReplyFor(string locale)
    {
        if (locale.Length < 2) return IdentityReplyEn;
        var key = locale[..2];
        return LocalisedIdentityReplies.TryGetValue(key, out var s) ? s : IdentityReplyEn;
    }

    private ValueTask PublishAsync(PhoneNumber to, string text, string locale, string contextHint)
    {
        var scrubbed = FeedbackResponseService.ScrubForOutbox(
            text, feedbackOptions.Value.OutboxTextMaxChars);
        var safe = LocaleValidator.Sanitize(locale);
        return bus.PublishAsync(new AnswerPlatformQuestionCommand(to, scrubbed, safe, contextHint));
    }
}
