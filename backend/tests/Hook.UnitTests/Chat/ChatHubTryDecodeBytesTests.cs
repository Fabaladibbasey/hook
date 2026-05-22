using Hook.Features.ChatSession;
using Shouldly;

namespace Hook.UnitTests.Chat;

public class ChatHubTryDecodeBytesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryDecodeBytes_NullOrEmpty_ReturnsFalse(string? input)
    {
        var ok = ChatHub.TryDecodeBytes(input, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeFalse();
        bytes.ShouldBeEmpty();
    }

    [Fact]
    public void TryDecodeBytes_ExactBoundary_LengthCheckPasses()
    {
        // (200/3 + 1)*4 + 4 = 276 — the upper allocation guard.
        // 200 bytes encode to ~268 chars (with padding). Just below the guard.
        var raw = new byte[200];
        new Random(42).NextBytes(raw);
        var b64 = Convert.ToBase64String(raw);

        b64.Length.ShouldBeLessThan(276);
        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeTrue();
        bytes.Length.ShouldBe(200);
        bytes.ShouldBe(raw);
    }

    [Fact]
    public void TryDecodeBytes_PastLengthGuard_ReturnsFalse_NoAllocation()
    {
        // Any b64 longer than (maxLen/3 + 1)*4 + 4 must be rejected before
        // allocating the byte buffer — length-attack defense.
        var pad = new string('A', 1000); // way over (200/3+1)*4+4 = 276
        var ok = ChatHub.TryDecodeBytes(pad, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeFalse();
        bytes.ShouldBeEmpty();
    }

    [Fact]
    public void TryDecodeBytes_DecodedBelowMin_ReturnsFalse()
    {
        var raw = new byte[5];
        var b64 = Convert.ToBase64String(raw);

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 10, maxLen: 200, out var bytes);

        ok.ShouldBeFalse();
        bytes.ShouldBeEmpty();
    }

    [Fact]
    public void TryDecodeBytes_DecodedAboveMax_ReturnsFalse()
    {
        // 200 bytes fits length-guard for maxLen=200 but if we check max=50 we get rejected.
        var raw = new byte[100];
        var b64 = Convert.ToBase64String(raw);

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 50, out var bytes);

        ok.ShouldBeFalse();
        bytes.ShouldBeEmpty();
    }

    [Fact]
    public void TryDecodeBytes_DecodedAtMin_ReturnsTrue()
    {
        var raw = new byte[10];
        new Random(1).NextBytes(raw);
        var b64 = Convert.ToBase64String(raw);

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 10, maxLen: 200, out var bytes);

        ok.ShouldBeTrue();
        bytes.Length.ShouldBe(10);
    }

    [Fact]
    public void TryDecodeBytes_DecodedAtMax_ReturnsTrue()
    {
        var raw = new byte[200];
        new Random(2).NextBytes(raw);
        var b64 = Convert.ToBase64String(raw);

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeTrue();
        bytes.Length.ShouldBe(200);
    }

    [Fact]
    public void TryDecodeBytes_PaddedInput_SlicesBuffer()
    {
        // 5 bytes → encoded with `=` padding, buffer over-allocates → slice branch.
        var raw = new byte[] { 1, 2, 3, 4, 5 };
        var b64 = Convert.ToBase64String(raw);
        b64.ShouldEndWith("=");

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeTrue();
        bytes.Length.ShouldBe(5);
        bytes.ShouldBe(raw);
    }

    [Fact]
    public void TryDecodeBytes_NoPaddingExactBuffer_NoSlice()
    {
        // Multiple of 3 raw bytes → b64 length is multiple of 4 with no `=`;
        // decoded length equals buffer length → no-slice branch.
        var raw = new byte[] { 1, 2, 3, 4, 5, 6 };
        var b64 = Convert.ToBase64String(raw);
        b64.ShouldNotContain("=");

        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeTrue();
        bytes.Length.ShouldBe(6);
        bytes.ShouldBe(raw);
    }

    [Theory]
    [InlineData("!!!!")]
    [InlineData("ABC@")]
    [InlineData("ABC")] // length % 4 != 0
    public void TryDecodeBytes_InvalidBase64_ReturnsFalse(string b64)
    {
        var ok = ChatHub.TryDecodeBytes(b64, minLen: 1, maxLen: 200, out var bytes);

        ok.ShouldBeFalse();
        bytes.ShouldBeEmpty();
    }
}
