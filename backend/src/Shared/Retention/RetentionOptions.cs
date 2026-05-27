using System.ComponentModel.DataAnnotations;

namespace Hook.Shared.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    [Range(1, 365)]
    public int RetentionDays { get; init; } = 7;

    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(24);

    [Range(typeof(TimeSpan), "00:00:00", "00:30:00")]
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(1);

    // Wolverine's dead-letter queue holds JSON envelopes (which may include user text
    // and unmasked phone for AI-stage messages) indefinitely after MaxAttempts failure.
    // The sweep prunes rows older than this window so PII does not accumulate at rest.
    [Range(1, 365)]
    public int DeadLetterRetentionDays { get; init; } = 7;

    // Pending MatchFeedback rows where the user never replied past this window get
    // bulk-claimed Skipped. Default 1d is well past Feedback:ParseRetryWindow (1h) so
    // the live-path parser can never race the sweep; the row is dead anyway.
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan PendingFeedbackClaimAfter { get; init; } = TimeSpan.FromDays(1);

    // Tiny per-(phone, question-hash) rows lose their dedup signal past the
    // PlatformAnswerOptions.DedupWindowSeconds horizon — prune at a much
    // shorter cadence than the 7d global retention so the table stays bounded
    // under question-spam load without waiting a week.
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan PlatformAnswerDedupCleanupAfter { get; init; } = TimeSpan.FromHours(1);

    public bool Enabled { get; init; } = true;
}
