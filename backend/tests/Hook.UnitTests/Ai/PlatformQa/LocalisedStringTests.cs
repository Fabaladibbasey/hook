using System.Collections.Frozen;
using Hook.Features.Ai.PlatformQa;
using Shouldly;

namespace Hook.UnitTests.Ai.PlatformQa;

public class LocalisedStringTests
{
    // Fixture with both a 2-letter "mn" key and a 3-letter "mnk" key so
    // precedence and fall-through are independently testable.
    private static FrozenDictionary<string, string> Table() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mn"]  = "two-letter-mn",
            ["mnk"] = "three-letter-mnk",
            ["fr"]  = "french",
        }.ToFrozenDictionary();

    [Fact]
    public void For_ThreeLetterLocale_WinsOverTwoLetterPrefix()
    {
        // Regression guard: "mnk" must NOT truncate to "mn" and hit the wrong slot.
        LocalisedString.For("mnk", Table(), "default").ShouldBe("three-letter-mnk");
    }

    [Fact]
    public void For_TwoLetterLocale_HitsExactSlot()
    {
        LocalisedString.For("mn", Table(), "default").ShouldBe("two-letter-mn");
    }

    [Fact]
    public void For_ThreeLetterLocaleNotInTable_FallsBackToDefault()
    {
        LocalisedString.For("de", Table(), "default").ShouldBe("default");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")] // length < 2
    public void For_NullEmptyOrTooShort_ReturnsDefault(string? locale)
    {
        LocalisedString.For(locale, Table(), "default").ShouldBe("default");
    }
}
