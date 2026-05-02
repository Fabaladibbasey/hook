namespace Hook.Features.MetaTemplates;

public interface IWhatsappContactRepository
{
    Task<DateTimeOffset?> GetLastInboundAtAsync(string phone, CancellationToken ct = default);
    Task UpsertInboundAsync(string phone, DateTimeOffset at, CancellationToken ct = default);
}
