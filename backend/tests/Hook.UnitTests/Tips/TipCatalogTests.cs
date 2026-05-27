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
    public void Keys_FitInMetricTagBudget()
    {
        // Tip keys flow into metric tags + log fields; cap at 64 chars to keep
        // dashboards legible. (Cooldown is now per-trigger jsonb, not per-key.)
        foreach (var tip in TipCatalog.ByTrigger.SelectMany(kv => kv.Value))
        {
            tip.Key.Length.ShouldBeLessThanOrEqualTo(64,
                $"key '{tip.Key}' exceeds metric-tag length budget (64).");
        }
    }

    [Fact]
    public void UserRequested_BucketIsNonEmpty()
    {
        TipCatalog.ByTrigger[TipTrigger.UserRequested].Count.ShouldBeGreaterThanOrEqualTo(3,
            "user-requested bucket too small — repeated TIP within cooldown returns 'no new tip' for all picks");
    }

    [Fact]
    public void Keys_AreGloballyUnique_AcrossTriggers()
    {
        // Globally-unique keys keep metric tags + log fields unambiguous so an
        // operator can see "which tip fired" without disambiguating by trigger.
        var all = TipCatalog.ByTrigger.SelectMany(kv => kv.Value).ToArray();
        all.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Length);
    }
}
