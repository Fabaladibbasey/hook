using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;

namespace Hook.Features.Matching.Match;

public static class ExpandedSlugsExtensions
{
    // Provider has a child of the requested slug -> the query was *narrowed* to
    // a child slug to include a more-specific provider (e.g. request "doctor",
    // provider "cardiology"). Otherwise the query was *broadened* to the parent
    // to include a more-general provider (e.g. request "cardiology", provider
    // "doctor"). Query construction guarantees overlap with one of the three
    // buckets, so non-Exact + non-Narrowed is Broadened by elimination.
    public static MatchKind Classify(this ExpandedSlugs slugs, IReadOnlyCollection<string> providerServices)
    {
        if (providerServices.Contains(slugs.Requested)) return MatchKind.Exact;
        if (slugs.Children.Any(providerServices.Contains)) return MatchKind.Narrowed;
        return MatchKind.Broadened;
    }
}
