using System.Security.Cryptography;
using System.Text;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Shouldly;

namespace Hook.UnitTests.Whatsapp;

public class SignatureValidatorTests
{
    private const string Secret = "super-secret-app-secret";

    [Fact]
    public void IsValid_ShouldAcceptCorrectSignature()
    {
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var header = "sha256=" + Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

        SignatureValidator.IsValid(header, body, Secret).ShouldBeTrue();
    }

    [Fact]
    public void IsValid_ShouldRejectTamperedBody()
    {
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var header = "sha256=" + Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
        var tampered = Encoding.UTF8.GetBytes("{\"hello\":\"WORLD\"}");

        SignatureValidator.IsValid(header, tampered, Secret).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5=abcd")]
    [InlineData("sha256=zzzz")]
    public void IsValid_ShouldRejectMissingOrMalformedHeader(string? header)
    {
        var body = Encoding.UTF8.GetBytes("payload");

        SignatureValidator.IsValid(header, body, Secret).ShouldBeFalse();
    }

    [Fact]
    public void IsValid_ShouldRejectEmptySecret()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var header = "sha256=" + Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes("any"), body));

        SignatureValidator.IsValid(header, body, string.Empty).ShouldBeFalse();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
