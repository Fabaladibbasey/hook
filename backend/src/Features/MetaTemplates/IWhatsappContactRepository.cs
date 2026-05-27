using Hook.Features.Tips;

namespace Hook.Features.MetaTemplates;

public sealed record ContactTipState(IReadOnlyDictionary<TipTrigger, DateTimeOffset> Cooldowns);

public interface IWhatsappContactRepository
{
    Task<DateTimeOffset?> GetLastInboundAtAsync(string phone, CancellationToken ct = default);
    Task UpsertInboundAsync(string phone, DateTimeOffset at, CancellationToken ct = default);
    Task<ContactTipState?> GetForTipsAsync(string phone, CancellationToken ct = default);
    Task RecordTipAsync(string phone, TipTrigger trigger, DateTimeOffset at, CancellationToken ct = default);
    Task<string> GetPreferredLocaleAsync(string phone, CancellationToken ct = default);
    Task UpdatePreferredLocaleAsync(string phone, string locale, CancellationToken ct = default);
}
