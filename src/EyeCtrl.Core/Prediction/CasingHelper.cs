namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Provides utilities for handling word casing normalization and ALL-CAPS detection.
/// </summary>
public static class CasingHelper
{
    // Thread-safe lock for mutations to the acronym set
    private static readonly object _acronymLock = new object();

    // Common acronyms that should remain ALL-CAPS
    private static readonly HashSet<string> KnownAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "UK", "NATO", "FBI", "CIA", "NASA", "UN", "EU", "WHO", "IMF",
        "HTML", "CSS", "JSON", "XML", "HTTP", "HTTPS", "FTP", "SQL", "API",
        "PDF", "USB", "GPS", "DVD", "CD", "TV", "AM", "PM", "CEO", "CFO",
        "CTO", "HR", "IT", "PR", "QA", "R&D", "VIP", "ASAP", "FAQ"
    };

    /// <summary>
    /// Checks if a string is all uppercase letters (2+ letters).
    /// </summary>
    /// <param name="text">The text to check.</param>
    /// <returns>True if the text is all uppercase with 2 or more letters.</returns>
    public static bool IsAllCaps(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= 1)
            return false;

        var letterCount = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                if (char.IsLower(c))
                    return false;
                letterCount++;
            }
        }

        return letterCount > 1;
    }

    /// <summary>
    /// Normalizes ALL-CAPS words to title case, preserving known acronyms.
    /// </summary>
    /// <param name="word">The word to normalize.</param>
    /// <returns>Title-cased word if it was ALL-CAPS, or the original word.</returns>
    public static string? NormalizeAllCaps(string? word)
    {
        if (string.IsNullOrEmpty(word))
            return word;

        // If not ALL-CAPS, return as-is
        if (!IsAllCaps(word))
            return word;

        // If it's a known acronym, keep it as-is
        if (IsKnownAcronym(word))
            return word;

        // Convert to title case: first letter upper, rest lower
        return CapitalizationHelper.Capitalize(word.ToLowerInvariant());
    }

    /// <summary>
    /// Checks if a word is a known acronym that should remain ALL-CAPS.
    /// </summary>
    /// <param name="word">The word to check.</param>
    /// <returns>True if the word is a known acronym.</returns>
    public static bool IsKnownAcronym(string word)
    {
        lock (_acronymLock)
        {
            return KnownAcronyms.Contains(word);
        }
    }

    /// <summary>
    /// Adds a custom acronym to the known acronyms set.
    /// Useful for domain-specific acronyms that should remain ALL-CAPS.
    /// Thread-safe for concurrent access.
    /// </summary>
    /// <param name="acronym">The acronym to add.</param>
    public static void AddAcronym(string acronym)
    {
        if (!string.IsNullOrWhiteSpace(acronym))
        {
            lock (_acronymLock)
            {
                KnownAcronyms.Add(acronym);
            }
        }
    }
}
