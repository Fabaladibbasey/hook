using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.ContactSharing.Events;
using Hook.Features.Matching.Match;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Wolverine;
using IMatchRepository = Hook.Features.Matching.MatchAggregate.IMatchRepository;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.Features.Matching.PresentMatches;

public sealed class MatchPresenter(
    IConversationAi ai,
    IWhatsappClient whatsapp,
    IMatchRepository matches,
    IProviderAvailabilityRepository providers,
    IMessageBus bus,
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

        var lines = batch.Scored
            .Select((s, i) => $"{i + 1}. {Mask(s.Candidate.Phone)} — {s.Candidate.DistanceKm:F1}km away (score {s.Score:F2})")
            .ToList();

        var facts = new Dictionary<string, string>
        {
            ["service"] = serviceSlug,
            ["matches"] = string.Join("; ", lines),
            ["count"] = batch.Scored.Count.ToString()
        };

        var ctx = new ReplyContext(
            Purpose: "present-top-matches",
            RecentTurns: Array.Empty<ConversationTurn>(),
            LanguageHint: "en",
            Facts: facts);

        var fallbackLines = batch.Scored
            .Select((s, i) => $"{i + 1}. {Mask(s.Candidate.Phone)} — {s.Candidate.DistanceKm:F1}km away")
            .ToList();
        var pickHint = batch.Scored.Count == 1
            ? "Reply PICK 1 to share contact."
            : $"Reply PICK 1 to PICK {batch.Scored.Count} to share contact, NEXT for more, or NEW for a different service.";
        var fallback = $"Top matches for {serviceSlug}:\n{string.Join("\n", fallbackLines)}\n{pickHint}";

        var reply = await AiReplyHelper.TryGenerateOrFallbackAsync(ai, ctx, "match_presenter", fallback, logger, ct);
        await whatsapp.SendTextAsync(clientPhone, reply, ct);

        await BroadcastToProvidersAsync(clientPhone, batch, serviceSlug, ct);
    }

    private async Task BroadcastToProvidersAsync(PhoneNumber clientPhone, MatchBatch batch, string serviceSlug, CancellationToken ct)
    {
        var existing = await matches.GetForRequestAsync(batch.RequestId, ct);
        var alreadyNotified = existing
            .Where(m => m.ContactShared)
            .Select(m => m.ProviderPhone)
            .ToHashSet();
        var anyPriorBroadcast = alreadyNotified.Count > 0;

        MatchEntity? representativeMatch = null;
        var dirty = false;

        foreach (var match in batch.NewMatches)
        {
            if (alreadyNotified.Contains(match.ProviderPhone)) continue;

            var provider = await providers.GetAsync(match.ProviderPhone, ct);
            if (provider is null) continue;
            if (!provider.ShareContact) continue;

            if (!PhoneNumber.TryParse(match.ProviderPhone, out var providerPhone))
            {
                logger.LogWarning("Skipping fan-out: invalid provider phone for match {MatchId}", match.Id);
                continue;
            }

            await whatsapp.SendTextAsync(providerPhone,
                $"Client wants {serviceSlug} ({clientPhone.Value}). Expect a message.", ct);

            match.ContactShared = true;
            alreadyNotified.Add(match.ProviderPhone);
            representativeMatch ??= match;
            dirty = true;
        }

        if (dirty) await matches.SaveChangesAsync(ct);

        if (!anyPriorBroadcast && representativeMatch is not null)
        {
            await bus.PublishAsync(new ContactExchanged(
                representativeMatch.Id,
                batch.RequestId,
                clientPhone.Value,
                representativeMatch.ProviderPhone));
        }
    }

    private static string Mask(string phone) =>
        PhoneNumber.TryParse(phone, out var p) ? p.Mask() : phone;
}
