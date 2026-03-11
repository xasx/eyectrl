namespace EyeCtrl.Core.Prediction;

using System.Globalization;

public static class LanguageDetector
{
    private static readonly HashSet<string> SupportedCodes = ["en", "fr", "es", "de", "pl"];

    public static string DetectFromCulture(CultureInfo culture)
    {
        var twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return SupportedCodes.Contains(twoLetter) ? twoLetter : "en";
    }

    public static string Detect() => DetectFromCulture(CultureInfo.CurrentUICulture);
}
