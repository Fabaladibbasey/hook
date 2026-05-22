namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class JudgeParentDedup
{
    public string Slug { get; private init; } = string.Empty;
    public DateTimeOffset JudgedAt { get; private set; }

    public static JudgeParentDedup Stamp(string slug, DateTimeOffset now) =>
        new() { Slug = slug, JudgedAt = now };
}
