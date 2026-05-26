using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Ai.PlatformQa;

public sealed class PlatformAnswerOptions
{
    public const string SectionName = "PlatformAnswer";

    // Tighter than the shared Reply cap (200): the PlatformAnswerSystem prompt
    // caps replies at ~60 words ≈ 90 tokens, so a smaller ceiling avoids
    // off-script rambling and trims inference time.
    [Range(1, 4096)]
    public int MaxOutputTokens { get; set; } = 100;

    // Window over which a duplicate (phone, question-hash) is suppressed.
    [Range(1, 600)]
    public int DedupWindowSeconds { get; set; } = 60;
}
