namespace Hook.Features.Ai.Models;

public enum IntentKind
{
    Unknown,
    ProviderRegistration,
    ServiceRequest,
    MatchSelection,
    NextMatches,
    IncreaseRange,
    Confirmation,
    Rejection,
    Edit,
    Cancel,
    FeedbackResponse,
    Greeting
}
