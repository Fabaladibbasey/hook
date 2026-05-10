using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Feedback;

public sealed class FeedbackOptions
{
    public const string SectionName = "Feedback";

    // Lower bound is 00:00:00 so integration tests can fire the prompt synchronously.
    [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
    public TimeSpan Step1InitialDelay { get; init; } = TimeSpan.FromMinutes(30);

    // Fallback delay used when the client says "in progress" but did not (or could not)
    // give an ETA we could parse. The ETA-driven path schedules the Step2 recheck
    // relative to the captured ETA itself; this is the no-ETA backstop.
    [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
    public TimeSpan Step2InProgressRecheckDelay { get; init; } = TimeSpan.FromHours(20);

    // Slop added on top of a parsed ETA so the recheck fires just after the client
    // expects the job to be done — absorbs Wolverine scheduler tick jitter and
    // small clock drift on the client phone.
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan EtaScheduleBuffer { get; init; } = TimeSpan.FromMinutes(5);

    [Range(typeof(TimeSpan), "00:00:30", "1.00:00:00")]
    public TimeSpan ParseRetryWindow { get; init; } = TimeSpan.FromHours(1);

    // Cap on how far in the future a parsed ETA can sit before we treat it as a
    // hallucination and fall back to Step2InProgressRecheckDelay. 7d covers any
    // realistic "in progress" job; anything beyond is almost certainly garbage.
    [Range(typeof(TimeSpan), "01:00:00", "30.00:00:00")]
    public TimeSpan MaxEtaHorizon { get; init; } = TimeSpan.FromDays(7);
}
