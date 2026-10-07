namespace EyeCtrl.Core.Localization;

using EyeCtrl.Core.Enums;

/// <summary>
/// The one mapping between a <see cref="UiLanguage"/> and every way the outside world spells it:
/// the settings JSON tokens (<c>system</c>/<c>german</c>/<c>english</c>), the <c>--ui-language</c>
/// command-line tokens (<c>system</c>/<c>de</c>/<c>en</c>), and the UI endonyms
/// (<c>System</c>/<c>Deutsch</c>/<c>English</c>). T04 and T05b use only this mapping.
/// </summary>
public static class UiLanguageTokens
{
    /// <summary>The settings-JSON token for <paramref name="language"/>.</summary>
    /// <param name="language">The language to spell.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UiLanguage"/>.</exception>
    public static string ToJson(UiLanguage language) => language switch
    {
        UiLanguage.System => "system",
        UiLanguage.German => "german",
        UiLanguage.English => "english",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Not a defined UiLanguage.")
    };

    /// <summary>Parses a settings-JSON token (case-insensitively).</summary>
    /// <param name="token">The token to parse; null or unknown yields <c>false</c>.</param>
    /// <param name="language">The parsed language when the token is known.</param>
    /// <returns>Whether the token named a language.</returns>
    public static bool TryFromJson(string? token, out UiLanguage language)
    {
        if (string.Equals(token, "system", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.System;
            return true;
        }
        if (string.Equals(token, "german", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.German;
            return true;
        }
        if (string.Equals(token, "english", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.English;
            return true;
        }
        language = UiLanguage.System;
        return false;
    }

    /// <summary>The <c>--ui-language</c> command-line token for <paramref name="language"/>.</summary>
    /// <param name="language">The language to spell.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UiLanguage"/>.</exception>
    public static string ToCommandLine(UiLanguage language) => language switch
    {
        UiLanguage.System => "system",
        UiLanguage.German => "de",
        UiLanguage.English => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Not a defined UiLanguage.")
    };

    /// <summary>Parses a <c>--ui-language</c> token (case-insensitively).</summary>
    /// <param name="token">The token to parse; null or unknown yields <c>false</c>.</param>
    /// <param name="language">The parsed language when the token is known.</param>
    /// <returns>Whether the token named a language.</returns>
    public static bool TryFromCommandLine(string? token, out UiLanguage language)
    {
        if (string.Equals(token, "system", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.System;
            return true;
        }
        if (string.Equals(token, "de", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.German;
            return true;
        }
        if (string.Equals(token, "en", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.English;
            return true;
        }
        language = UiLanguage.System;
        return false;
    }

    /// <summary>The endonym shown wherever a language is chosen — readable in every language.</summary>
    /// <param name="language">The language to name.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UiLanguage"/>.</exception>
    public static string Endonym(UiLanguage language) => language switch
    {
        UiLanguage.System => "System",
        UiLanguage.German => "Deutsch",
        UiLanguage.English => "English",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Not a defined UiLanguage.")
    };
}
