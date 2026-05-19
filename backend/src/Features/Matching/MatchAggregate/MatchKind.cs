namespace Hook.Features.Matching.MatchAggregate;

public enum MatchKind
{
    /// <summary>Provider's services list contains the request's exact slug.</summary>
    Exact,

    /// <summary>
    /// Provider matched the parent of the requested slug -- the query broadened
    /// upward to include a more-general provider (e.g. request "cardiology",
    /// provider lists "doctor"). Score discounted by <c>BroadenedMatchFactor</c>.
    /// </summary>
    Broadened,

    /// <summary>
    /// Provider matched a child of the requested slug -- the query narrowed
    /// downward to include a more-specific provider (e.g. request "doctor",
    /// provider lists "cardiology"). Score discounted by <c>NarrowedMatchFactor</c>.
    /// </summary>
    Narrowed,
}
