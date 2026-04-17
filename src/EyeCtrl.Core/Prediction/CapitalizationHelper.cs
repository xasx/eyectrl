namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Provides smart capitalization based on sentence boundaries.
/// </summary>
public static class CapitalizationHelper
{
    private static readonly HashSet<char> SentenceEndMarkers = new() { '.', '!', '?' };

    /// <summary>
    /// Determines if the next word should be capitalized based on context.
    /// </summary>
    /// <param name="currentText">The text typed so far.</param>
    /// <returns>True if the next word should be capitalized.</returns>
    public static bool ShouldCapitalize(string currentText)
    {
        return ShouldCapitalize(currentText, "en");
    }

    /// <summary>
    /// Determines if the next word should be capitalized based on context.
    /// </summary>
    /// <param name="currentText">The text typed so far.</param>
    /// <param name="languageCode">Two-letter ISO language code for language-specific abbreviations.</param>
    /// <returns>True if the next word should be capitalized.</returns>
    public static bool ShouldCapitalize(string currentText, string languageCode)
    {
        if (string.IsNullOrEmpty(currentText))
            return true;

        // Trim trailing whitespace to find the last meaningful character
        var trimmed = currentText.TrimEnd();
        if (trimmed.Length == 0)
            return true;

        var lastChar = trimmed[^1];

        // Check if last character is a sentence-ending marker
        if (SentenceEndMarkers.Contains(lastChar))
        {
            // Check if this is likely an abbreviation
            if (lastChar == '.' && trimmed.Length > 1)
            {
                // Get language-specific abbreviations
                var abbreviations = AbbreviationProvider.GetAbbreviations(languageCode);

                // Look for pattern: period preceded by a known abbreviation
                var beforePeriod = trimmed[..^1].TrimEnd();
                if (beforePeriod.Length > 0)
                {
                    var words = beforePeriod.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 0)
                    {
                        var lastWord = words[^1];

                        // Check if it's a known abbreviation
                        if (abbreviations.Contains(lastWord))
                            return false;

                        // Also check for single-letter abbreviations (e.g., "A.", "B.")
                        // or short lowercase words (likely abbreviations like "i.e.", "e.g.")
                        if (lastWord.Length <= 3 && char.IsLower(lastWord[0]))
                            return false;
                    }
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Capitalizes the first letter of a string.
    /// </summary>
    /// <param name="word">The word to capitalize.</param>
    /// <returns>The word with the first letter capitalized.</returns>
    public static string Capitalize(string word)
    {
        if (string.IsNullOrEmpty(word))
            return string.Empty;

        if (word.Length == 1)
            return char.ToUpperInvariant(word[0]).ToString();

        // Find the first letter character
        for (int i = 0; i < word.Length; i++)
        {
            if (char.IsLetter(word[i]))
            {
                if (i == 0)
                    return char.ToUpperInvariant(word[0]) + word[1..];
                else
                    return word[..i] + char.ToUpperInvariant(word[i]) + word[(i + 1)..];
            }
        }

        // No letters found, return as-is
        return word;
    }

    /// <summary>
    /// Applies smart capitalization to a word based on context.
    /// Preserves existing capitalization if word is already capitalized.
    /// </summary>
    /// <param name="word">The word to potentially capitalize.</param>
    /// <param name="currentText">The text typed so far.</param>
    /// <returns>The word with appropriate capitalization.</returns>
    public static string ApplySmartCapitalization(string word, string currentText)
    {
        return ApplySmartCapitalization(word, currentText, "en");
    }

    /// <summary>
    /// Applies smart capitalization to a word based on context.
    /// Preserves existing capitalization if word is already capitalized.
    /// </summary>
    /// <param name="word">The word to potentially capitalize.</param>
    /// <param name="currentText">The text typed so far.</param>
    /// <param name="languageCode">Two-letter ISO language code for language-specific abbreviations.</param>
    /// <returns>The word with appropriate capitalization.</returns>
    public static string ApplySmartCapitalization(string word, string currentText, string languageCode)
    {
        if (string.IsNullOrEmpty(word))
            return string.Empty;

        // If word is already capitalized, preserve it
        if (word.Length > 0 && char.IsUpper(word[0]))
            return word;

        // If we should capitalize, do so
        if (ShouldCapitalize(currentText, languageCode))
            return Capitalize(word);

        return word;
    }
}
