namespace Hook.Features.Matching.MatchAggregate;

public class Match
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }
    public required string ProviderPhone { get; init; }
    public required string ServiceSlug { get; init; }
    public double DistanceKm { get; init; }
    public double Score { get; init; }
    public bool ContactShared { get; set; }
    public Guid? ChatId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
