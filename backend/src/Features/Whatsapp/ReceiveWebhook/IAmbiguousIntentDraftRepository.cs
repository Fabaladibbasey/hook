namespace Hook.Features.Whatsapp.ReceiveWebhook;

public interface IAmbiguousIntentDraftRepository
{
    Task<AmbiguousIntentDraft?> GetAsync(string phone, CancellationToken ct = default);
    Task UpsertAsync(AmbiguousIntentDraft draft, CancellationToken ct = default);
    Task DeleteAsync(string phone, CancellationToken ct = default);
}
