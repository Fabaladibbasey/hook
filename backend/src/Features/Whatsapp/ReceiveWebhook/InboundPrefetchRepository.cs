using Hook.Features.Feedback.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundPrefetchRepository(HookDbContext db)
{
    // Sequential reads on the scoped context: one connection, one round-trip
    // each, no fanout against the Npgsql pool. Earlier parallel-on-dbFactory
    // implementation demanded up to 5 connections per inbound — under webhook
    // saturation that exceeded the pool. Postgres handles these tiny PK-keyed
    // lookups in single-digit ms each; the sequential wall-clock is dominated
    // by network RTT, not throughput. activeRequest must stay TRACKED on this
    // scoped context so the router can call .Close() and SaveChanges via
    // Wolverine AutoApplyTransactions.
    public async Task<InboundPrefetch> GetAllAsync(string phone, CancellationToken ct)
    {
        var registration = await db.RegistrationDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct);

        var client = await db.ClientRequestDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct);

        var ambiguous = await db.AmbiguousIntentDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Phone == phone, ct);

        var pendingFeedback = await (
            from f in db.MatchFeedback.AsNoTracking()
            where f.Answer == FeedbackAnswer.Pending
                && db.ServiceRequests.Any(r => r.Id == f.RequestId && r.ClientPhone == phone)
            orderby f.PromptedAt descending
            select f).FirstOrDefaultAsync(ct);

        var activeRequest = await db.ServiceRequests
            .Where(r => r.ClientPhone == phone && r.Status != ServiceRequestStatus.Closed)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new InboundPrefetch(registration, client, ambiguous, pendingFeedback, activeRequest);
    }
}
