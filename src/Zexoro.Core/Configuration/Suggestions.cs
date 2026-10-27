namespace Zexoro.Configuration;

/// <summary>Finds the closest known name for "did you mean" hints.</summary>
public static class Suggestions
{
    /// <summary>
    /// Returns the candidate closest to <paramref name="value"/> by edit distance (ignoring case,
    /// and treating '-' and '_' alike), or null if none is close enough to be a likely typo.
    /// </summary>
    public static string? Closest(string value, IEnumerable<string> candidates)
    {
        var normalized = Normalize(value);
        var list = candidates.ToList();
        if (normalized.Length >= 3)
        {
            var prefixed = list.Where(c => Normalize(c).StartsWith(normalized, StringComparison.Ordinal) && c != value).ToList();
            if (prefixed.Count == 1)
            {
                return prefixed[0];
            }
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in list)
        {
            var distance = Distance(normalized, Normalize(candidate));
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        var limit = Math.Max(1, Math.Min(normalized.Length, 12) / 3);
        return best is not null && bestDistance <= limit && best != value ? best : null;
    }

    private static string Normalize(string s) => s.ToUpperInvariant().Replace('-', '_');

    /// <summary>Optimal string alignment distance: insertions, deletions, substitutions and adjacent transpositions.</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
