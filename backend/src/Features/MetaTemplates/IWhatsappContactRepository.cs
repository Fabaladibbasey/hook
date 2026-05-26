namespace Hook.Features.MetaTemplates;

public sealed record ContactTipState(string LastTipKey, DateTimeOffset LastTipAt);

public interface IWhatsappContactRepository
{
    Task<DateTimeOffset?> GetLastInboundAtAsync(string phone, CancellationToken ct = default);
    Task UpsertInboundAsync(string phone, DateTimeOffset at, CancellationToken ct = default);
    Task<ContactTipState?> GetForTipsAsync(string phone, CancellationToken ct = default);
    Task RecordTipAsync(string phone, string tipKey, DateTimeOffset at, CancellationToken ct = default);
}
