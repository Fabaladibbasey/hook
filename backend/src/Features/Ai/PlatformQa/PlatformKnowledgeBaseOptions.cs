using System.ComponentModel.DataAnnotations;

namespace Hook.Features.Ai.PlatformQa;

public sealed class PlatformKnowledgeBaseOptions
{
    public const string SectionName = "PlatformKb";

    [Range(1000, 64000)]
    public int MaxKbChars { get; set; } = 16000;
}
