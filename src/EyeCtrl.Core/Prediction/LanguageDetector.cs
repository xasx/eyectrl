namespace EyeCtrl.Core.Prediction;

using System.Globalization;

/// <summary>Detects the user's language from system culture settings for dictionary selection.</summary>
public static class LanguageDetector
{
    private static readonly HashSet<string> SupportedCodes = ["en", "fr", "es", "de", "pl"];

    /// <summary>Detects the language code from a specific <see cref="CultureInfo"/>.</summary>
    /// <param name="culture">The culture to extract the language from.</param>
    /// <returns>A supported two-letter ISO language code, or "en" if unsupported.</returns>
    public static string DetectFromCulture(CultureInfo culture)
    {
        var twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return SupportedCodes.Contains(twoLetter) ? twoLetter : "en";
    }

    /// <summary>Detects the language code from the current UI culture.</summary>
    /// <returns>A supported two-letter ISO language code, or "en" if unsupported.</returns>
    public static string Detect() => DetectFromCulture(CultureInfo.CurrentUICulture);
}
