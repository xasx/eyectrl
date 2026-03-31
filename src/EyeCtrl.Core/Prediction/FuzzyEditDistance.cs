namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Computes a keyboard-aware fuzzy edit distance between strings, based on the Levenshtein distance
/// algorithm with modified costs for physically adjacent key substitutions.
/// </summary>
/// <remarks>
/// <para>
/// This algorithm extends the classic Levenshtein (edit) distance by incorporating knowledge of
/// keyboard layout to more accurately measure typing errors. It recognizes that typos involving
/// adjacent keys (e.g., "helo" for "hello", where 'l' and 'o' are adjacent) are more likely than
/// arbitrary substitutions, and assigns them a lower cost.
/// </para>
/// <para>
/// <b>Cost Structure:</b><br/>
/// - Exact character match: 0.0 (no cost)<br/>
/// - Adjacent-key substitution: 0.5 (common typo, reduced penalty)<br/>
/// - Non-adjacent substitution: 1.0 (less likely, full penalty)<br/>
/// - Insertion: 1.0 (missing character)<br/>
/// - Deletion: 1.0 (extra character)
/// </para>
/// <para>
/// <b>Use Cases:</b><br/>
/// - Gaze-typed word prediction and correction<br/>
/// - Fuzzy string matching for UI commands<br/>
/// - Spelling correction with typing error awareness<br/>
/// - Auto-completion ranking
/// </para>
/// <para>
/// <b>Performance:</b> O(m × n) time complexity, O(m × n) space complexity,<br/>
/// where m and n are the lengths of the two input strings.
/// </para>
/// <para>
/// <b>Algorithm Background:</b><br/>
/// Based on the Levenshtein distance algorithm (Wagner-Fischer algorithm, 1974), which uses
/// dynamic programming to compute the minimum edit distance between two strings.<br/>
/// Original paper: Wagner, R. A., &amp; Fischer, M. J. (1974). "The String-to-String Correction Problem."
/// Journal of the ACM, 21(1), 168-173.<br/>
/// DOI: <see href="https://doi.org/10.1145/321796.321811">10.1145/321796.321811</see><br/>
/// Wikipedia: <see href="https://en.wikipedia.org/wiki/Levenshtein_distance">https://en.wikipedia.org/wiki/Levenshtein_distance</see>
/// </para>
/// <para>
/// The keyboard-aware modification is inspired by research in spell-checking and typing error analysis,
/// particularly work on context-sensitive spelling correction and fat-finger error modeling.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Load keyboard adjacency map for QWERTY layout
/// var adjacencyMap = KeyboardAdjacencyMap.For("QWERTY");
///
/// // Compute fuzzy distance between strings
/// double distance1 = FuzzyEditDistance.Compute("hello", "helo", adjacencyMap);
/// // Returns ~1.0 (one deletion)
///
/// double distance2 = FuzzyEditDistance.Compute("hello", "hrllo", adjacencyMap);
/// // Returns ~0.5 (e→r substitution, r and e are adjacent on QWERTY)
///
/// double distance3 = FuzzyEditDistance.Compute("hello", "hallo", adjacencyMap);
/// // Returns ~1.0 (e→a substitution, not adjacent keys)
///
/// // Use in word prediction to rank candidates
/// var candidates = new[] { "hello", "help", "held" };
/// var typed = "helo";
/// var ranked = candidates
///     .Select(word => new { Word = word, Distance = FuzzyEditDistance.Compute(typed, word, adjacencyMap) })
///     .OrderBy(x => x.Distance)
///     .ToList();
/// // ranked[0].Word will be "hello" with lowest distance
/// </code>
/// </example>
public static class FuzzyEditDistance
{
    /// <summary>
    /// Computes the keyboard-aware fuzzy edit distance between two strings.
    /// </summary>
    /// <param name="a">The first string to compare.</param>
    /// <param name="b">The second string to compare.</param>
    /// <param name="adjacencyMap">
    /// A dictionary mapping each character to its set of physically adjacent keys on the keyboard.
    /// Obtain this from <see cref="KeyboardAdjacencyMap.For"/> with your keyboard layout name
    /// (e.g., "QWERTY", "QWERTZ", "AZERTY").
    /// </param>
    /// <returns>
    /// The minimum edit distance as a double, where:
    /// - 0.0 means the strings are identical
    /// - Lower values indicate greater similarity
    /// - Adjacent-key substitutions add 0.5 to the distance
    /// - Other operations (insertion, deletion, non-adjacent substitution) add 1.0
    /// </returns>
    /// <remarks>
    /// <para>
    /// The algorithm uses dynamic programming to build a matrix where dp[i,j] represents the minimum
    /// edit distance between the first i characters of string a and the first j characters of string b.
    /// The final answer is found in dp[m,n] where m and n are the lengths of the input strings.
    /// </para>
    /// <para>
    /// Character comparison is case-insensitive (both strings are converted to lowercase internally).
    /// </para>
    /// <para>
    /// <b>Edge Cases:</b><br/>
    /// - If either string is empty, returns the length of the other string (all insertions/deletions)
    /// - Handles Unicode characters properly via char.ToLowerInvariant()
    /// </para>
    /// </remarks>
    public static double Compute(
        string a,
        string b,
        Dictionary<char, HashSet<char>> adjacencyMap)
    {
        int m = a.Length, n = b.Length;

        // Base cases: if one string is empty, distance equals the length of the other
        if (m == 0) return n;
        if (n == 0) return m;

        // Initialize DP table: dp[i,j] = minimum edit distance between a[0..i-1] and b[0..j-1]
        var dp = new double[m + 1, n + 1];

        // Base case: distance from empty string to prefixes of a (all deletions)
        for (int i = 0; i <= m; i++) dp[i, 0] = i;

        // Base case: distance from empty string to prefixes of b (all insertions)
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        // Fill DP table using recurrence relation
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                char ca = char.ToLowerInvariant(a[i - 1]);
                char cb = char.ToLowerInvariant(b[j - 1]);

                // Determine substitution cost based on character relationship
                double subCost = ca == cb ? 0.0  // Exact match: no cost
                    : KeyboardAdjacencyMap.AreAdjacent(adjacencyMap, ca, cb) ? 0.5  // Adjacent keys: reduced cost
                    : 1.0;  // Non-adjacent: full cost

                // Recurrence: minimum of three operations
                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1.0,      // deletion: remove character from 'a'
                        dp[i, j - 1] + 1.0),      // insertion: add character to match 'b'
                    dp[i - 1, j - 1] + subCost);  // substitution: replace character in 'a' with character from 'b'
            }
        }

        return dp[m, n];
    }
}
