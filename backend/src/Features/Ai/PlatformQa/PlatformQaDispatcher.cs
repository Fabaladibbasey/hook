using System.Text;
using System.Text.RegularExpressions;
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
        if (!IdentityPhraseBook.Phrases.TryGetValue(canonical, out var phraseLocale))
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
        var hash = PlatformAnswerDedupKey.Of(canonical);
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

    private static readonly Regex FormatCharRx =
        new(@"[\p{Cf}\p{Mn} ـ]+", RegexOptions.Compiled);

    internal static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // NFKC: maps full-width letters (ｗ→w), NBSP (U+00A0→space), etc.
        // Curly-apostrophe fold stays after NFKC (font variants survive decomp).
        var canonical = text.Normalize(NormalizationForm.FormKC)
            .Replace('’', '\'')
            .Replace('‘', '\'');
        // Strip format chars (Cf: ZWNJ U+200C, ZWJ U+200D, RLO U+202E, …),
        // non-spacing combining marks (Mn), regular space, and Arabic tatweel
        // (U+0640) so invisible variants cannot bypass IdentityPhrases lookup.
        canonical = FormatCharRx.Replace(canonical, " ");
        var lowered = canonical.Trim().ToLowerInvariant().TrimEnd('?', '؟').TrimEnd();
        return string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string IdentityReplyFor(string locale) =>
        LocalisedString.For(locale, IdentityPhraseBook.LocalisedReplies, IdentityPhraseBook.FallbackEn);

    internal static string IdentityReplyForTest(string locale) => IdentityReplyFor(locale);

    private ValueTask PublishAsync(PhoneNumber to, string text, string locale, string contextHint)
    {
        var scrubbed = FeedbackResponseService.ScrubForOutbox(
            text, feedbackOptions.Value.OutboxTextMaxChars);
        var safe = LocaleValidator.Sanitize(locale);
        return bus.PublishAsync(new AnswerPlatformQuestionCommand(to, scrubbed, safe, contextHint));
    }
}
