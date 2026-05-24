namespace Hook.Features.Feedback;

internal static class FeedbackConstants
{
    public const string PendingUniqueIndexName = "ux_match_feedback_pending";
    public const string RequestStep1UniqueIndexName = "ux_match_feedback_request_step1";
    public const string PendingPromptedAtIndexName = "ix_match_feedback_pending_prompted_at";

    // Mirrored by HasMaxLength(500) on MatchFeedback.NoReason in FeedbackConfigurations.
    // Migration column width also 500 — schema is the source of truth, this constant
    // bounds the runtime truncation before the row hits the DB.
    public const int NoReasonMaxLength = 500;
}
