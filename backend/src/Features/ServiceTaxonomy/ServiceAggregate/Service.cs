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
    private readonly List<string> _rawExamples = new();
    public IReadOnlyList<string> RawExamples => _rawExamples;

    public bool IsRoot => ParentSlug is null;

    public void RememberRawExample(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var trimmed = raw.Trim();
        if (trimmed.Length > MaxRawExampleLength) trimmed = trimmed[..MaxRawExampleLength];
        if (_rawExamples.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;
        if (_rawExamples.Count >= 10) _rawExamples.RemoveAt(0);
        _rawExamples.Add(trimmed);
    }

    public void AssignParent(Service parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!parent.IsRoot)
            throw new InvalidOperationException(
                $"Parent {parent.Slug} is not a root sector; only one-level hierarchies allowed.");
        if (string.Equals(parent.Slug, Slug, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service {Slug} cannot be its own parent.");
        if (ParentSlug is not null)
            throw new InvalidOperationException(
                $"Service {Slug} already has parent {ParentSlug}; re-parent rejected to prevent A→B→A cycles.");
        ParentSlug = parent.Slug;
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
