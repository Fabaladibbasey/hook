using Hook.Features.Matching.Match;
using Hook.Features.Matching.PresentMatches.Dispatch;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Wolverine;

namespace Hook.Features.Matching.PresentMatches;

public sealed class MatchPresenter(IMessageBus bus)
{
    // Routes presentation through the durable outbox so the AI presenter inside
    // PresentMatchesHandler runs off the user-visible critical path. Empty batches
    // ack inline (no AI needed).
    public ValueTask PresentAsync(
        PhoneNumber clientPhone,
        MatchBatch batch,
        string serviceSlug,
        CancellationToken ct = default)
    {
        if (batch.Scored.Count == 0)
        {
            return bus.PublishAsync(new SendWhatsAppTextRequested(clientPhone,
                "No providers found nearby. Reply INCREASE to widen the search or NEW to change the service."));
        }

        var dtos = batch.Scored
            .Select(s => new MatchPresentationDto(
                s.Candidate.Phone, s.Candidate.DistanceKm, s.Score, s.Kind))
            .ToList();

        return bus.PublishAsync(new PresentMatchesRequested(
            clientPhone, batch.RequestId, serviceSlug, dtos));
    }
}
