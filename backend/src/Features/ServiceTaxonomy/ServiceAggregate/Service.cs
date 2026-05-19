using Hook.Shared.Domain;

namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public class Service : IAggregateRoot
{
    // Bounds storage and prompt size on the AI parent-judge path. Each entry is
    // truncated before insert so a flood of long inbounds cannot inflate the row
    // or starve the LLM's context budget.
    public const int MaxRawExampleLength = 200;

    public required string Slug { get; init; }
    public string? ParentSlug { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<string> RawExamples { get; private set; } = [];

    public bool IsRoot => ParentSlug is null;

    public void RememberRawExample(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var trimmed = raw.Trim();
        if (trimmed.Length > MaxRawExampleLength) trimmed = trimmed[..MaxRawExampleLength];
        if (RawExamples.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;
        if (RawExamples.Count >= 10) RawExamples.RemoveAt(0);
        RawExamples.Add(trimmed);
    }

    public void AssignParent(string parentSlug)
    {
        if (string.IsNullOrWhiteSpace(parentSlug))
            throw new ArgumentException("Parent slug required.", nameof(parentSlug));
        if (string.Equals(parentSlug, Slug, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service {Slug} cannot be its own parent.");
        if (ParentSlug is not null)
            throw new InvalidOperationException(
                $"Service {Slug} already has parent {ParentSlug}; re-parent rejected to prevent A→B→A cycles.");
        ParentSlug = parentSlug;
    }

    public static Service Create(string slug, string rawExample = "")
    {
        var svc = new Service
        {
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (rawExample.Length > 0) svc.RememberRawExample(rawExample);
        return svc;
    }
}
