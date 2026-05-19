using Hook.Shared.Domain;

namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public class Service : IAggregateRoot
{
    public required string Slug { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<string> RawExamples { get; private set; } = [];

    public void RememberRawExample(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var trimmed = raw.Trim();
        if (RawExamples.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;
        if (RawExamples.Count >= 10) RawExamples.RemoveAt(0);
        RawExamples.Add(trimmed);
    }

    public static Service Create(string slug, string? rawExample = null)
    {
        var svc = new Service
        {
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (rawExample is not null) svc.RememberRawExample(rawExample);
        return svc;
    }
}
