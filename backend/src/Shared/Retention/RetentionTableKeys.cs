namespace Hook.Shared.Retention;

public static class RetentionTableKeys
{
    public const string ChatSessions = "chat_sessions";
    public const string ServiceRequests = "service_requests";
    public const string ProviderAvailabilities = "provider_availabilities";
    public const string WhatsappContacts = "whatsapp_contacts";
    public const string GeocodeCache = "geocode_cache";
    public const string ProviderRegistrationDrafts = "provider_registration_drafts";
    public const string ClientRequestDrafts = "client_request_drafts";
    public const string AmbiguousIntentDrafts = "ambiguous_intent_drafts";
    public const string MatchFeedback = "match_feedback";
    public const string MatchFeedbackPendingClaimed = "match_feedback_pending_claimed";
    public const string PlatformAnswerDedup = "platform_answer_dedup";
    public const string WolverineDeadLetters = "wolverine_dead_letters";
}
