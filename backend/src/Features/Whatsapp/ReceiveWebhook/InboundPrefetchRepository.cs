using Hook.Features.Feedback.Models;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed class InboundPrefetchRepository(
    HookDbContext db,
    IDbContextFactory<HookDbContext> dbFactory)
{
    public async Task<InboundPrefetch> GetAllAsync(string phone, CancellationToken ct)
    {
        var registrationTask = ReadAsync(c => c.RegistrationDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct), ct);

        var clientTask = ReadAsync(c => c.ClientRequestDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Phone == phone, ct), ct);

        var ambiguousTask = ReadAsync(c => c.AmbiguousIntentDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Phone == phone, ct), ct);

        var feedbackTask = ReadAsync(c => (
            from f in c.MatchFeedback.AsNoTracking()
            where f.Answer == FeedbackAnswer.Pending
                && c.ServiceRequests.Any(r => r.Id == f.RequestId && r.ClientPhone == phone)
            orderby f.PromptedAt descending
            select f).FirstOrDefaultAsync(ct), ct);

        // activeRequest tracks on the scoped context so router-side .Close() commits via
        // Wolverine AutoApplyTransactions SaveChanges. Only valid inside a handler context.
        var activeRequestTask = db.ServiceRequests
            .Where(r => r.ClientPhone == phone && r.Status != ServiceRequestStatus.Closed)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        await Task.WhenAll(registrationTask, clientTask, ambiguousTask, feedbackTask, activeRequestTask);

        return new InboundPrefetch(
            await registrationTask,
            await clientTask,
            await ambiguousTask,
            await feedbackTask,
            await activeRequestTask);
    }

    private async Task<T> ReadAsync<T>(Func<HookDbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scoped = await dbFactory.CreateDbContextAsync(ct);
        return await query(scoped);
    }
}
