namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public sealed record ExpandedSlugs(
    string Requested,
    string? Parent,
    IReadOnlyList<string> Children)
{
    // Materialised once at construction. Consumers use .Any / .Contains, so the
    // order of entries is not semantically meaningful — Requested first only for
    // human-readable debugging.
    public IReadOnlyList<string> All { get; } = BuildAll(Requested, Parent, Children);

    private static IReadOnlyList<string> BuildAll(string requested, string? parent, IReadOnlyList<string> children)
    {
        var list = new List<string>(1 + (parent is null ? 0 : 1) + children.Count) { requested };
        if (parent is not null) list.Add(parent);
        list.AddRange(children);
        return list.AsReadOnly();
    }
}
