namespace Hook.Features.Matching.MatchAggregate;

internal static class MatchLabels
{
    // User-facing tag appended to non-Exact matches in the WhatsApp multi-pick
    // list and Step1 "Which provider?" reply. Hierarchy-derived (Broadened /
    // Narrowed) matches share this single label so the bot doesn't leak the
    // expansion direction to the client.
    public const string Related = "Related";
}
