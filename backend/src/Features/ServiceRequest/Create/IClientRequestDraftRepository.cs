namespace Hook.Features.ServiceRequest.Create;

public interface IClientRequestDraftRepository
{
    Task<ClientRequestDraft?> GetAsync(string phone, CancellationToken ct = default);
    Task UpsertAsync(ClientRequestDraft draft, CancellationToken ct = default);
    Task DeleteAsync(string phone, CancellationToken ct = default);
}
