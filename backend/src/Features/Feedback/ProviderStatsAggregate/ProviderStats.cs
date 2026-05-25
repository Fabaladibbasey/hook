using Hook.Shared.Domain;

namespace Hook.Features.Feedback.ProviderStatsAggregate;

public class ProviderStats : IAggregateRoot
{
    public string ProviderPhone { get; private init; } = string.Empty;
    public int CompletedCount { get; private set; }
    public int SuccessCount { get; private set; }
    public double SuccessRate { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    public void RecordOutcome(bool success, DateTimeOffset now)
    {
        CompletedCount++;
        if (success) SuccessCount++;
        SuccessRate = CompletedCount == 0 ? 0 : (double)SuccessCount / CompletedCount;
        LastUpdated = now;
    }

    public static ProviderStats Initial(string phone, DateTimeOffset now) => new()
    {
        ProviderPhone = phone,
        LastUpdated = now
    };
}
