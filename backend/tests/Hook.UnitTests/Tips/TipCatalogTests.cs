using Hook.Features.Tips;
using Shouldly;

namespace Hook.UnitTests.Tips;

public class TipCatalogTests
{
    [Fact]
    public void EveryTrigger_HasAtLeastOneTip()
    {
        foreach (var trigger in Enum.GetValues<TipTrigger>())
        {
            TipCatalog.ByTrigger.ContainsKey(trigger).ShouldBeTrue(
                $"trigger {trigger} has no tips configured");
            TipCatalog.ByTrigger[trigger].ShouldNotBeEmpty(
                $"trigger {trigger} maps to an empty list");
        }
    }

    [Fact]
    public void Keys_AreUnique_WithinEachTrigger()
    {
        foreach (var (_, tips) in TipCatalog.ByTrigger)
        {
            tips.Select(t => t.Key).Distinct().Count().ShouldBe(tips.Count);
        }
    }

    [Fact]
    public void Keys_FitInColumn()
    {
        foreach (var tip in TipCatalog.ByTrigger.SelectMany(kv => kv.Value))
        {
            tip.Key.Length.ShouldBeLessThanOrEqualTo(64,
                $"key '{tip.Key}' exceeds LastTipKey column maxLength (64).");
        }
    }

    [Fact]
    public void Keys_AreGloballyUnique_AcrossTriggers()
    {
        // Cooldown indexing keys on the contact's LastTipKey only — a key reused
        // across triggers would silently share a cooldown across both.
        var all = TipCatalog.ByTrigger.SelectMany(kv => kv.Value).ToArray();
        all.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }
}
