namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Keyboard-aware Levenshtein distance. Adjacent-key substitutions cost 0.5 instead of 1.0.
/// Insertions and deletions always cost 1.0.
/// </summary>
public static class FuzzyEditDistance
{
    public static double Compute(
        string a,
        string b,
        Dictionary<char, HashSet<char>> adjacencyMap)
    {
        int m = a.Length, n = b.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        var dp = new double[m + 1, n + 1];

        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                char ca = char.ToLowerInvariant(a[i - 1]);
                char cb = char.ToLowerInvariant(b[j - 1]);

                double subCost = ca == cb ? 0.0
                    : KeyboardAdjacencyMap.AreAdjacent(adjacencyMap, ca, cb) ? 0.5
                    : 1.0;

                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1.0,      // deletion
                        dp[i, j - 1] + 1.0),      // insertion
                    dp[i - 1, j - 1] + subCost);  // substitution
            }
        }

        return dp[m, n];
    }
}
