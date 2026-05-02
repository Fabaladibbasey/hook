using System.Security.Cryptography;
using System.Text;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public static class SignatureValidator
{
    private const string Prefix = "sha256=";

    public static bool IsValid(string? signatureHeader, ReadOnlySpan<byte> body, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        if (string.IsNullOrEmpty(appSecret)) return false;
        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var providedHex = signatureHeader[Prefix.Length..];
        Span<byte> expected = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(Encoding.UTF8.GetBytes(appSecret), body, expected, out _))
            return false;

        Span<byte> provided = stackalloc byte[32];
        if (!TryParseHex(providedHex, provided)) return false;

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> bytes)
    {
        if (hex.Length != bytes.Length * 2) return false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = FromHex(hex[i * 2]);
            var lo = FromHex(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int FromHex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
