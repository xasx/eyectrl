namespace EyeCtrl.Core.Keyboard;

using System.Globalization;

/// <summary>
/// The T14 symbol-caption rule (user's pick, 2026-10-07): command keys whose whole caption is a
/// symbol draw at <see cref="Scale"/> the key font and in the theme's medium weight, so the
/// fallback faces' faint glyphs read beside the letters. A character key such as € types its
/// glyph and must look like the letters around it, so the rule covers command keys only.
/// </summary>
public static class SymbolCaptionRule
{
    /// <summary>
    /// Multiplier applied to the CLAMPED key font. The user picked x1.4, SemiBold, from the T14
    /// symbol-legibility sheet.
    /// </summary>
    public const double Scale = 1.4;

    /// <summary>
    /// The fit limit's edge padding in DIP (the same 2 DIP as A68): a symbol caption's advance may
    /// use at most the key box minus this much on each side, so no symbol caption can clip.
    /// </summary>
    public const double EdgePadding = 2.0;

    /// <summary>
    /// True when the key's caption is a symbol caption:
    /// (a) the key has a non-empty <see cref="KeyDefinition.SpecialAction"/> (command keys only),
    /// and (b) the label is non-empty and EVERY text element is a non-ASCII code point whose
    /// Unicode category is MathSymbol (Sm) or OtherSymbol (So).
    /// <para>
    /// No letter, digit, space, ASCII character or punctuation may appear: "Bild↑", "+ Wort",
    /// "#+=", "123" and "F1" are all ordinary captions. No font lookup is involved.
    /// </para>
    /// </summary>
    /// <param name="key">The key whose caption is tested.</param>
    /// <returns>True if the caption takes the symbol treatment.</returns>
    public static bool IsSymbolCaption(KeyDefinition key)
    {
        if (string.IsNullOrEmpty(key.SpecialAction) || string.IsNullOrEmpty(key.Label)) return false;

        var elements = StringInfo.GetTextElementEnumerator(key.Label);
        while (elements.MoveNext())
        {
            var codePoint = char.ConvertToUtf32((string)elements.Current, 0);
            if (codePoint <= 0x7F) return false;

            var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            if (category is not (UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol)) return false;
        }

        return true;
    }
}
