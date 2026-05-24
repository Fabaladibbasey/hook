using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

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

    // Step1 re-check ladder when the user replies "still looking" with no specific
    // time. After Step1MaxRechecks consecutive ambiguous replies, claim Skipped
    // silently. FeedbackOptionsValidator enforces the cross-knob invariant.
    [Range(1, 10)]
    public int Step1MaxRechecks { get; init; } = 4;

    // Non-empty + size invariants enforced by FeedbackOptionsValidator.
    public IReadOnlyList<TimeSpan> Step1RecheckSchedule { get; init; } =
    [
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(48)
    ];

    // Floor between any two Step1 prompt sends for the same Pending row. Two
    // back-to-back rechecks (opportunistic chat-end firing milliseconds after a
    // scheduled re-fire) collapse to one prompt.
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan MinRecheckGap { get; init; } = TimeSpan.FromMinutes(5);
}

// Cross-knob validator: Step1MaxRechecks must not exceed the ladder length so
// the dispatcher never has to silently saturate at the last rung.
internal sealed class FeedbackOptionsValidator : IValidateOptions<FeedbackOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedbackOptions options)
    {
        if (options.Step1RecheckSchedule.Count == 0)
            return ValidateOptionsResult.Fail("Step1RecheckSchedule must contain at least one entry.");
        if (options.Step1MaxRechecks > options.Step1RecheckSchedule.Count)
            return ValidateOptionsResult.Fail(
                $"Step1MaxRechecks ({options.Step1MaxRechecks}) must be <= "
                + $"Step1RecheckSchedule.Count ({options.Step1RecheckSchedule.Count}).");
        return ValidateOptionsResult.Success;
    }
}
