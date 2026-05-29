using System.Text.Json;
using Hook.Features.Tips;
using Shouldly;

namespace Hook.UnitTests.Tips;

// Regression guard for the implicit coupling between two write paths and one read path:
//
//   Write path A (production):  WhatsappContactRepository.RecordTipAsync  →
//       raw SQL jsonb_set with ((int)trigger).ToString() key  →  "0"
//
//   Write path B (unused in prod, present in EF ValueConverter):  STJ default  →
//       enum NAME as key  →  "AfterWelcome"  ← asymmetric vs Write path A
//
//   Read path (production):  STJ Deserialize via WhatsappContactConfiguration.JsonOpts  →
//       accepts BOTH "0" and "AfterWelcome" → enum value
//
// The asymmetry is safe today only because the EF tracker never writes through the
// converter (WhatsappContact.TipCooldowns setter is private; all writes go through
// raw SQL). If a tracker write ever fires, the dict key flips from "0" to
// "AfterWelcome" and a cooldown lookup against a co-existing SQL-written "0" key
// silently misses. These tests freeze the asymmetry as known + document the
// production contract: ordinal-int-string keys must round-trip through STJ read.
public class TipCooldownSerializationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(); // mirrors WhatsappContactConfiguration

    [Fact]
    public void Deserialize_OrdinalIntegerKey_RoundTrips_ProductionWriteFormat()
    {
        // Mirrors the raw jsonb_set output from RecordTipAsync:
        // jsonb_set('{}', '{0}', '"2026-01-01T00:00:00+00:00"').
        // This is the production-critical contract — SQL writes ordinal, STJ must read it.
        const string json = """{"0":"2026-01-01T00:00:00+00:00"}""";
        var dict = JsonSerializer.Deserialize<Dictionary<TipTrigger, DateTimeOffset>>(json, JsonOpts);
        dict.ShouldNotBeNull();
        dict.ShouldContainKey(TipTrigger.AfterWelcome);
        dict[TipTrigger.AfterWelcome].ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Deserialize_EnumNameKey_AlsoRoundTrips_UnusedInProdButPossible()
    {
        // STJ also accepts the NAME key on read. Documents that if the asymmetric
        // write path ever fires (EF tracker save), a subsequent read still parses.
        const string json = """{"AfterWelcome":"2026-01-01T00:00:00+00:00"}""";
        var dict = JsonSerializer.Deserialize<Dictionary<TipTrigger, DateTimeOffset>>(json, JsonOpts);
        dict.ShouldNotBeNull();
        dict.ShouldContainKey(TipTrigger.AfterWelcome);
    }

    [Fact]
    public void Serialize_TipTriggerDictionary_EmitsEnumName_NotOrdinal()
    {
        // Asymmetric write contract: STJ default writes the enum NAME, while
        // RecordTipAsync's raw SQL writes the ordinal. Tracker writes are not
        // exercised in prod (private setter), so this asymmetry is dormant.
        // Pin the current STJ behavior so a future JsonStringEnumConverter or
        // JsonNumberHandling change is forced through this test first.
        var dict = new Dictionary<TipTrigger, DateTimeOffset>
        {
            [TipTrigger.AfterWelcome] = DateTimeOffset.UnixEpoch
        };
        var json = JsonSerializer.Serialize(dict, JsonOpts);
        json.ShouldContain("\"AfterWelcome\"");
        json.ShouldNotContain("\"0\"");
    }
}
