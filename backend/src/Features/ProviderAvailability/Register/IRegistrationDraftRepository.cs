namespace Hook.Features.ProviderAvailability.Register;

public interface IRegistrationDraftRepository
{
    Task<RegistrationDraft?> GetAsync(string phone, CancellationToken ct = default);
    Task UpsertAsync(RegistrationDraft draft, CancellationToken ct = default);
    Task DeleteAsync(string phone, CancellationToken ct = default);
}
