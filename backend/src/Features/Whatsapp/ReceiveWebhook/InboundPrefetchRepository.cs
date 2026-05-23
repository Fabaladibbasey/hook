using Hook.Features.Feedback.Models;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundPrefetchRepository(HookDbContext db)
{
    // Lazy single-shot lookups on the scoped context. Router calls them in the
    // order it consumes them and short-circuits on the first hit — the happy
    // path (active registration draft) becomes one RTT instead of five. Each
    // method runs sequentially on the scoped context: one connection, no fanout
    // against the Npgsql pool. ActiveRequest stays TRACKED so the router can
    // call .Close() and SaveChanges via Wolverine AutoApplyTransactions.
    public Task<RegistrationDraft?> GetRegistrationDraftAsync(string phone, CancellationToken ct) =>
        db.RegistrationDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct);

    public Task<ClientRequestDraft?> GetClientDraftAsync(string phone, CancellationToken ct) =>
        db.ClientRequestDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct);

    public Task<AmbiguousIntentDraft?> GetAmbiguousDraftAsync(string phone, CancellationToken ct) =>
        db.AmbiguousIntentDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Phone == phone, ct);

    public Task<MatchFeedback?> GetPendingFeedbackAsync(string phone, CancellationToken ct) =>
        (from f in db.MatchFeedback.AsNoTracking()
         where f.Answer == FeedbackAnswer.Pending
             && db.ServiceRequests.Any(r => r.Id == f.RequestId && r.ClientPhone == phone)
         orderby f.PromptedAt descending
         select f).FirstOrDefaultAsync(ct);

    public Task<ServiceRequest.RequestAggregate.ServiceRequest?> GetActiveRequestAsync(
        string phone,
        CancellationToken ct) =>
        db.ServiceRequests
            .Where(r => r.ClientPhone == phone && r.Status != ServiceRequestStatus.Closed)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
