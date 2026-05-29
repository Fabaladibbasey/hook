using Hook.Features.Ai.PlatformQa;
using Shouldly;

namespace Hook.UnitTests.Ai.PlatformQa;

public class LocaleValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_NullOrEmpty_ReturnsEn(string? raw)
    {
        LocaleValidator.Sanitize(raw).ShouldBe("en");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("wol")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void Sanitize_ValidShapes_ReturnsAsIs(string raw)
    {
        LocaleValidator.Sanitize(raw).ShouldBe(raw);
    }

    [Theory]
    [InlineData("en\nNew instruction")]
    [InlineData("en US")]
    [InlineData("EN")]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("e")]
    [InlineData("123456")]
    [InlineData("en-us")]
    public void Sanitize_InvalidShapes_ReturnsEn(string raw)
    {
        LocaleValidator.Sanitize(raw).ShouldBe("en");
    }
}
