using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching.Match;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Matching.PresentMatches;

public sealed class MatchPresenter(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    ILogger<MatchPresenter> logger)
{
    public async Task PresentAsync(PhoneNumber clientPhone, MatchBatch batch, string serviceSlug, CancellationToken ct = default)
    {
        if (batch.Scored.Count == 0)
        {
            await whatsapp.SendTextAsync(clientPhone,
                "No providers found nearby. Reply INCREASE to widen the search or NEW to change the service.", ct);
            return;
        }

        const int MaxPresented = 5;
        var presented = batch.Scored.Take(MaxPresented)
            .Select((s, i) => new
            {
                n        = i + 1,
                phone    = Mask(s.Candidate.Phone),
                distance = Math.Round(s.Candidate.DistanceKm, 1),
                score    = Math.Round(s.Score, 2)
            })
            .ToArray();

        var facts = new Dictionary<string, string>
        {
            ["service"] = serviceSlug,
            ["matches"] = JsonSerializer.Serialize(presented),
            ["count"]   = presented.Length.ToString()
        };

        var ctx = new ReplyContext(
            Purpose: "present-top-matches",
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: facts);

        var fallbackLines = batch.Scored
            .Select((s, i) => $"{i + 1}. {Mask(s.Candidate.Phone)} — {s.Candidate.DistanceKm:F1}km away")
            .ToList();
        // Bot owns the call-to-action verbatim — the AI presenter only writes the
        // body. Keeping the action line deterministic guarantees the vocabulary
        // (PICK / NEXT / NEW) matches the InboundRouter's intent detectors, so
        // user replies always route correctly regardless of how the LLM phrases
        // the body.
        var pickHint = batch.Scored.Count == 1
            ? "Reply PICK 1 to connect with this provider. NEXT for more, NEW for a different service."
            : $"Reply PICK 1 (or e.g. PICK 1,2 or PICK ALL) to connect with one or more providers. NEXT for more, NEW for a different service.";
        var fallback = $"Top matches for {serviceSlug}:\n{string.Join("\n", fallbackLines)}";

        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, "match_presenter", fallback, logger, ct);
        await whatsapp.SendTextAsync(clientPhone, $"{reply}\n\n{pickHint}", ct);
    }

    private static string Mask(string phone) =>
        PhoneNumber.TryParse(phone, out var p) ? p.Mask() : phone;
}
