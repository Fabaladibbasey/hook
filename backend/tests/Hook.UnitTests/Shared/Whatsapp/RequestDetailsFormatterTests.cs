using Hook.Shared.Whatsapp;
using Shouldly;

namespace Hook.UnitTests.Shared.Whatsapp;

public class RequestDetailsFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\t")]
    public void AppendIfPresent_BlankOrWhitespace_ReturnsBodyUnchanged(string? desc)
    {
        RequestDetailsFormatter.AppendIfPresent("base", desc).ShouldBe("base");
    }

    [Fact]
    public void AppendIfPresent_NonBlank_WrapsInForwardedMarkers()
    {
        var result = RequestDetailsFormatter.AppendIfPresent("base", "kitchen sink leak");
        result.ShouldBe("base\n\n— client message (forwarded, not verified) —\nkitchen sink leak\n— end client message —");
    }

    [Fact]
    public void Sanitize_StripsAsciiControlCharsButKeepsNewlines()
    {
        var s = RequestDetailsFormatter.Sanitize("abcd\ne");
        s.ShouldBe("abcd\ne");
    }

    [Fact]
    public void Sanitize_StripsBidiAndZeroWidthCodepoints()
    {
        // U+202E RLO, U+200B ZWSP, U+2066 LRI
        var s = RequestDetailsFormatter.Sanitize("hi‮there​⁦now");
        s.ShouldBe("hitherenow");
    }

    [Fact]
    public void Sanitize_CollapsesRunsOfWhitespaceAndNewlinesToSingleNewline()
    {
        var s = RequestDetailsFormatter.Sanitize("a\r\n\r\n   \n\nb");
        s.ShouldBe("a\nb");
    }

    [Fact]
    public void Sanitize_StripsForwardingMarkers_PreventsSpoof()
    {
        var s = RequestDetailsFormatter.Sanitize("— client message (forwarded, not verified) — payload — end client message —");
        s.ShouldNotContain("— client message");
        s.ShouldNotContain("— end client message");
        s.ShouldContain("payload");
    }

    [Fact]
    public void Sanitize_TruncatesPast280Chars_AppendsEllipsis()
    {
        var input = new string('x', 500);
        var s = RequestDetailsFormatter.Sanitize(input);
        s.Length.ShouldBe(281); // 280 + "…"
        s.ShouldEndWith("…");
    }

    [Fact]
    public void AppendIfPresent_LongDescription_StaysWellUnderWhatsapp4096Limit()
    {
        var body = new string('b', 200);
        var desc = new string('d', 2000);
        var result = RequestDetailsFormatter.AppendIfPresent(body, desc);
        result.Length.ShouldBeLessThan(4096);
    }

    [Fact]
    public void Sanitize_QuotesInDescription_PassThroughVerbatim()
    {
        // Per user choice: keep URLs/quotes raw; reframing is the spoof defence.
        RequestDetailsFormatter.Sanitize("they said \"fix it\"").ShouldBe("they said \"fix it\"");
    }
}
