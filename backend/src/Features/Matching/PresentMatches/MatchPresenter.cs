using System.Text.Json;
using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Matching.Match;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Options;

namespace Hook.Features.Matching.PresentMatches;

public sealed class MatchPresenter(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    IOptions<MatchingOptions> options,
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

        // Defense-in-depth: even though MatchingService is expected to truncate, we
        // re-apply the cap here so a regression upstream can't leak extra phones.
        var cap = options.Value.TopMatchesPerBatch;
        var capped = batch.Scored.Take(cap).ToList();

        var presented = capped
            .Select((s, i) => new
            {
                n = i + 1,
                phone = Mask(s.Candidate.Phone),
                distance = Math.Round(s.Candidate.DistanceKm, 1),
                score = Math.Round(s.Score, 2)
            })
            .ToArray();

        var facts = new Dictionary<string, string>
        {
            ["service"] = serviceSlug,
            ["matches"] = JsonSerializer.Serialize(presented),
            ["count"] = presented.Length.ToString()
        };

        var ctx = new ReplyContext(
            Purpose: "present-top-matches",
            RecentTurns: [],
            LanguageHint: "en",
            Facts: facts);

        var fallbackLines = capped
            .Select((s, i) => $"{i + 1}. {Mask(s.Candidate.Phone)} — {s.Candidate.DistanceKm:F1}km away")
            .ToList();
        // Bot owns the call-to-action verbatim — the AI presenter only writes the
        // body. Keeping the action line deterministic guarantees the vocabulary
        // (PICK / NEXT / NEW) matches the InboundRouter's intent detectors, so
        // user replies always route correctly regardless of how the LLM phrases
        // the body.
        var pickHint = capped.Count == 1
            ? "Reply PICK 1 to connect with this provider. NEXT for more, NEW for a different service."
            : $"Reply PICK 1 (or e.g. PICK 1,2 or PICK ALL) to connect with one or more providers. NEXT for more, NEW for a different service.";
        var fallback = $"Top matches for {serviceSlug}:\n{string.Join("\n", fallbackLines)}";

        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, "match_presenter", fallback, logger, ct);
        await whatsapp.SendTextAsync(clientPhone, $"{reply}\n\n{pickHint}", ct);
    }

    private static string Mask(string phone) =>
        PhoneNumber.TryParse(phone, out var p) ? p.Mask() : phone;
}
