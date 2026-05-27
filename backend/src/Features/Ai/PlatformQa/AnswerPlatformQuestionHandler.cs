using System.Collections.Frozen;
using Hook.Features.Observability;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Ai.PlatformQa;

public sealed class AnswerPlatformQuestionHandler(
    IConversationAi ai,
    PlatformKnowledgeBase kb,
    IPlatformAnswerDedupGate dedup,
    IMessageBus bus,
    ILogger<AnswerPlatformQuestionHandler> logger)
{
    // Deterministic fallbacks when the AI returns null (transport failure, jailbreak
    // echo, empty body). A small per-language table is intentional — auto-translating
    // a fallback string is not safe, but a dozen reviewed lines is. English is the
    // default for unknown locales.
    internal const string Fallback =
        "I'm not sure about that. Try rephrasing, or send REQUEST / REGISTER to use the platform.";

    private static readonly FrozenDictionary<string, string> LocalisedFallbacks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fr"] = "Je ne suis pas sûr. Reformulez votre question, ou envoyez REQUEST / REGISTER pour utiliser la plateforme.",
            ["ar"] = "لست متأكدا. أعد صياغة سؤالك، أو أرسل REQUEST / REGISTER لاستخدام المنصة.",
            ["wo"] = "Xamuma. Soppi sa laaj, walla yónnee REQUEST / REGISTER ngir jëfandikoo plateforme bi.",
        }.ToFrozenDictionary();

    internal static string FallbackFor(string? locale) =>
        LocalisedString.For(locale, LocalisedFallbacks, Fallback);

    // [NonTransactional]: AI inference takes 60-150s; opt out of AutoApplyTransactions
    // so the handler doesn't pin an Npgsql connection across the Ollama window.
    // No DB mutation happens here — answering Q&A never advances a draft.
    [NonTransactional]
    public async Task Handle(AnswerPlatformQuestionCommand cmd, CancellationToken ct)
    {
        var reply = await ai.AnswerPlatformQuestionAsync(cmd.Question, cmd.Locale, kb.Content, ct);
        string body;
        if (string.IsNullOrWhiteSpace(reply))
        {
            HookMetrics.AiOutboundDropped.Add(1,
                new KeyValuePair<string, object?>("stage", "platform-answer"),
                new KeyValuePair<string, object?>("reason", "fallback"));
            logger.LogInformation(
                "PlatformAnswer fallback for {To} ctx={Ctx}", cmd.To.Mask(), cmd.ReplyContextHint);
            body = FallbackFor(cmd.Locale);
        }
        else
        {
            body = reply;
        }

        // Publish FIRST so a DLQ on the answer envelope does not silently
        // drop the user's answer on operator replay. Claim AFTER so the dedup
        // row reflects "we did send" rather than "we plan to send".
        await bus.PublishAsync(new SendWhatsAppTextCommand(cmd.To, body));

        // Post-publish best-effort cost-suppression record. Failure here is
        // swallowed — including OCE — because a Wolverine retry would re-run
        // Ollama AND re-publish a duplicate answer. The dedup row is best-effort
        // cost-suppression; user-facing delivery already happened.
        var hash = PlatformAnswerDedupKey.Of(cmd.Question);
        try
        {
            var claimed = await dedup.TryClaimAsync(cmd.To.Value, hash, ct);
            if (!claimed)
            {
                HookMetrics.AiOutboundDropped.Add(1,
                    new KeyValuePair<string, object?>("stage", "platform-answer"),
                    new KeyValuePair<string, object?>("reason", "dedup-row-present"));
                logger.LogDebug(
                    "PlatformAnswer dedup row already present post-publish for {To}", cmd.To.Mask());
            }
        }
        catch (Exception ex)
        {
            HookMetrics.AiOutboundDropped.Add(1,
                new KeyValuePair<string, object?>("stage", "platform-answer"),
                new KeyValuePair<string, object?>("reason", "dedup-persist-failed"));
            logger.LogWarning(ex, "PlatformAnswer dedup persist failed for {To}", cmd.To.Mask());
        }
    }
}
