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

    public bool Enabled { get; init; } = true;
}
