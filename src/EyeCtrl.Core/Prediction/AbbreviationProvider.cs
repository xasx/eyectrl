namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Provides language-specific abbreviations for smart capitalization.
/// </summary>
public static class AbbreviationProvider
{
    // Cache for abbreviation sets by language
    private static readonly Dictionary<string, HashSet<string>> _abbreviationCache = new();

    /// <summary>
    /// Gets abbreviations for the specified language code.
    /// </summary>
    /// <param name="languageCode">Two-letter ISO language code (e.g., "en", "de", "fr").</param>
    /// <returns>A case-insensitive set of common abbreviations for the language.</returns>
    public static HashSet<string> GetAbbreviations(string languageCode)
    {
        var code = languageCode?.ToLowerInvariant() ?? "en";

        if (_abbreviationCache.TryGetValue(code, out var cached))
            return cached;

        var abbreviations = code switch
        {
            "en" => GetEnglishAbbreviations(),
            "de" => GetGermanAbbreviations(),
            "fr" => GetFrenchAbbreviations(),
            "es" => GetSpanishAbbreviations(),
            "pl" => GetPolishAbbreviations(),
            _ => GetEnglishAbbreviations() // Default to English
        };

        _abbreviationCache[code] = abbreviations;
        return abbreviations;
    }

    private static HashSet<string> GetEnglishAbbreviations()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Titles
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr",
            // Common abbreviations
            "etc", "vs", "i.e", "e.g", "a.m", "p.m",
            // Addresses
            "st", "ave", "blvd", "dept", "rd", "ln", "ct",
            // Business
            "inc", "ltd", "co", "corp",
            // Other
            "approx", "est", "vol", "no", "pp"
        };
    }

    private static HashSet<string> GetGermanAbbreviations()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Titles (German titles are often capitalized, but we check the lowercase form)
            "dr", "prof", "dipl", "ing",
            // Common abbreviations
            "z.b", "bzw", "u.a", "d.h", "usw", "evtl", "ggf",
            "inkl", "exkl", "ca", "max", "min",
            // Addresses
            "str", "pl", "geb",
            // Business
            "gmbh", "ag", "kg",
            // Other
            "mio", "mrd", "jh", "jgg", "bd", "s"
        };
    }

    private static HashSet<string> GetFrenchAbbreviations()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Titles
            "m", "mme", "mlle", "dr", "pr",
            // Common abbreviations
            "etc", "c.-à-d", "p.ex", "cf", "n.b",
            // Addresses
            "av", "bd", "pl", "r", "sq",
            // Business
            "cie", "sa", "sarl",
            // Other
            "env", "vol", "p", "pp"
        };
    }

    private static HashSet<string> GetSpanishAbbreviations()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Titles
            "sr", "sra", "srta", "dr", "dra", "prof",
            // Common abbreviations
            "etc", "p.ej", "aprox", "pág", "págs",
            // Addresses
            "av", "avda", "c", "pl", "pza",
            // Business
            "s.a", "s.l", "cía",
            // Other
            "vol", "cap", "ed", "núm", "n.º"
        };
    }

    private static HashSet<string> GetPolishAbbreviations()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Titles
            "dr", "prof", "mgr", "inż",
            // Common abbreviations
            "itd", "itp", "np", "tzn", "tj", "m.in", "wg",
            "ok", "r", "godz", "min", "sek",
            // Addresses
            "ul", "al", "pl", "os",
            // Business
            "sp", "z.o.o", "s.a",
            // Other
            "str", "nr", "art", "pkt", "por"
        };
    }
}
