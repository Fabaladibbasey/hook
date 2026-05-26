using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Tips;

public sealed class TipOptions
{
    public const string SectionName = "Tips";

    public bool Enabled { get; set; } = true;

    [Range(1, 720)]
    public int DefaultCooldownHours { get; set; } = 24;
}
