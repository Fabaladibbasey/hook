using System.Text.RegularExpressions;

namespace Hook.Features.Whatsapp.Phone;

public readonly record struct PhoneNumber
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{6,14}$", RegexOptions.Compiled);

    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Parse(string raw)
    {
        if (TryParse(raw, out var number)) return number;
        throw new FormatException($"Invalid phone number: '{raw}'.");
    }

    public static bool TryParse(string? raw, out PhoneNumber number)
    {
        number = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        var withPlus = trimmed.StartsWith('+') ? trimmed : "+" + trimmed.TrimStart('0');
        var compact = new string(withPlus.Where(c => c == '+' || char.IsDigit(c)).ToArray());

        if (!E164.IsMatch(compact)) return false;

        number = new PhoneNumber(compact);
        return true;
    }

    public string Mask()
    {
        if (Value.Length <= 5) return "***";
        return string.Concat(Value.AsSpan(0, 4), "***", Value.AsSpan(Value.Length - 2));
    }

    public override string ToString() => Value;
}
