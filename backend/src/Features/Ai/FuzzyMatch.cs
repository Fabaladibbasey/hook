namespace Hook.Features.Ai;

public static class FuzzyMatch
{
    public static bool MatchesAny(string? input, IReadOnlyList<string?> tokens, int maxDistance = 1)
    {
        if (string.IsNullOrEmpty(input) || tokens.Count == 0) return false;
        var s = input.ToLowerInvariant();
        foreach (var t in tokens)
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (Distance(s, t.ToLowerInvariant()) <= maxDistance) return true;
        }
        return false;
    }

    // Optimal String Alignment (restricted Damerau-Levenshtein): counts
    // insertions, deletions, substitutions, and adjacent transpositions.
    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // d[i, j] = OSA distance between a[0..i-1] and b[0..j-1]
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1,       // deletion
                             d[i, j - 1] + 1),       // insertion
                    d[i - 1, j - 1] + cost);          // substitution

                // transposition
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
