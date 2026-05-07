using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Feedback;

public sealed class FeedbackOptions
{
    public const string SectionName = "Feedback";

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Step2InitialDelay { get; init; } = TimeSpan.FromHours(20);

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Step2InProgressRecheckDelay { get; init; } = TimeSpan.FromHours(48);

    [Range(typeof(TimeSpan), "00:00:30", "1.00:00:00")]
    public TimeSpan ParseRetryWindow { get; init; } = TimeSpan.FromHours(1);
}
