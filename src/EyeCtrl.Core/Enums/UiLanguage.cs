namespace EyeCtrl.Core.Enums;

/// <summary>
/// Which language EyeCtrl's own UI is shown in. The JSON tokens, the <c>--ui-language</c>
/// command-line tokens and the endonyms are mapped in exactly one place,
/// <see cref="Localization.UiLanguageTokens"/>; nothing else may spell a language out.
/// </summary>
public enum UiLanguage
{
    /// <summary>Follow the Windows display language (German only when it is a German variant).</summary>
    System = 0,
    /// <summary>German (Deutsch), loaded from the loose <c>strings.de.json</c>.</summary>
    German = 1,
    /// <summary>English, compiled into <c>EyeCtrl.Core</c> so it can never go missing.</summary>
    English = 2
}
