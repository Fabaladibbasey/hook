using Hook.Features.Whatsapp.Phone;
using Shouldly;

namespace Hook.UnitTests.Whatsapp;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+22070000123", "+22070000123")]
    [InlineData("22070000123", "+22070000123")]
    [InlineData("  +220 7000 0000  ", "+22070000000")]
    [InlineData("00220700000000", "+220700000000")]
    [InlineData("+447700900123", "+447700900123")]  // parser contract: non-+220 country code accepted
    public void TryParse_ShouldNormalizeToE164(string raw, string expected)
    {
        PhoneNumber.TryParse(raw, out var phone).ShouldBeTrue();
        phone.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("+0123")]
    [InlineData("+1234567890123456")]
    public void TryParse_ShouldRejectInvalid(string raw)
    {
        PhoneNumber.TryParse(raw, out _).ShouldBeFalse();
    }

    [Fact]
    public void Mask_ShouldHideMiddleDigits()
    {
        var phone = PhoneNumber.Parse("+22070000123");

        phone.Mask().ShouldBe("+220***23");
    }
}
