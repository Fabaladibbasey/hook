namespace Hook.Features.Ai.Models;

public enum IntentKind
{
    Unknown,
    ProviderRegistration,
    ServiceRequest,
    MatchSelection,
    NextMatches,
    IncreaseRange,
    ShareContact,
    Confirmation,
    Rejection,
    Edit,
    Cancel,
    FeedbackResponse,
    Greeting
}
