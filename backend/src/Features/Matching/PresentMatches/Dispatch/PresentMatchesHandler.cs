using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Tips;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Attributes;

namespace Hook.Features.Matching.PresentMatches.Dispatch;

public sealed class PresentMatchesHandler(
    IConversationAi ai,
    IMessageBus bus,
    IOptions<MatchingOptions> options,
    ILogger<PresentMatchesHandler> logger)
{
    // [NonTransactional]: AI inference takes 60-150s; opt out of AutoApplyTransactions
    // so the handler does not pin an Npgsql connection across the Ollama window. Match
    // rows are read inline by MatchingService before publish, then carried in the envelope
    // so this handler is purely send-side.
    [NonTransactional]
    public async Task Handle(PresentMatchesCommand command, CancellationToken ct)
    {
        var cap = options.Value.TopMatchesPerBatch;
        var capped = command.Matches.Take(cap).ToList();
        if (capped.Count == 0)
        {
            await bus.PublishAsync(new SendWhatsAppTextCommand(command.ClientPhone,
                "No providers found nearby. Reply INCREASE to widen the search or NEW to change the service."));
            return;
        }

        var presented = capped
            .Select((m, i) => new
            {
                n = i + 1,
                phone = Mask(m.ProviderPhone),
                distance = Math.Round(m.DistanceKm, 1),
                score = Math.Round(m.Score, 2),
                label = m.Kind == MatchKind.Exact ? string.Empty : MatchLabels.Related
            })
            .ToArray();

        var facts = new Dictionary<string, string>
        {
            ["service"] = command.ServiceSlug,
            ["matches"] = JsonSerializer.Serialize(presented),
            ["count"] = presented.Length.ToString()
        };

        var ctx = new ReplyContext(
            Purpose: "present-top-matches",
            RecentTurns: [],
            LanguageHint: "en")
        {
            Facts = facts
        };

        var fallbackLines = capped
            .Select((m, i) =>
            {
                var tag = m.Kind == MatchKind.Exact ? string.Empty : $" ({MatchLabels.Related})";
                return $"{i + 1}. {Mask(m.ProviderPhone)} — {m.DistanceKm:F1}km away{tag}";
            })
            .ToList();
        // Bot owns the call-to-action verbatim — the AI presenter only writes the
        // body. Keeping the action line deterministic guarantees the vocabulary
        // (PICK / NEXT / NEW) matches the InboundRouter's intent detectors.
        var pickHint = capped.Count == 1
            ? "Reply PICK 1 to connect with this provider. NEXT for more, NEW for a different service."
            : "Reply PICK 1 (or e.g. PICK 1,2 or PICK ALL) to connect with one or more providers. "
                + "NEXT for more, NEW for a different service.";
        var fallback = $"Top matches for {command.ServiceSlug}:\n{string.Join("\n", fallbackLines)}";

        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, "match_presenter", fallback, logger, ct);
        await bus.PublishAsync(new SendWhatsAppTextCommand(
            command.ClientPhone, $"{reply}\n\n{pickHint}", Tip: TipTrigger.AfterMatchPresented));
    }

    private static string Mask(string phone) =>
        PhoneNumber.TryParse(phone, out var p) ? p.Mask() : phone;
}
