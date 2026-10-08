namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// The font half of the T14 symbol-caption rule: <see cref="Resolve"/> maps a key's clamped base
/// font to the font its label is drawn at, applying <see cref="SymbolCaptionRule.Scale"/> and the
/// T14 Phase-2 hand-off's fit limit. The rule's classification lives in
/// <see cref="SymbolCaptionRule"/>, in Core.
/// </summary>
internal static class SymbolCaptionFonts
{
    /// <summary>
    /// The pixels-per-DIP the rule was measured at (the reference probe and the user's 200 %
    /// display). It only rounds the fit decision, which runs against a 2 DIP margin twice the
    /// measured DPI sensitivity, so production uses the same value the designer measured with.
    /// </summary>
    private const double ReferencePixelsPerDip = 2.0;

    /// <summary>
    /// The two-line caption's word line as a fraction of the clamped base font (T14 round 2, B4,
    /// user's live-check pick: "⌫" over a small "Wort"). The 0.75 is the designer's measured ratio.
    /// </summary>
    private const double SubLabelFontRatio = 0.75;

    /// <summary>
    /// The exact font size <c>KeyboardWindow.RebuildKeys</c> gives a key's label: the base font
    /// unchanged for an ordinary caption, and for a symbol caption the base times
    /// <see cref="SymbolCaptionRule.Scale"/>, reduced just enough that the advance keeps
    /// <see cref="SymbolCaptionRule.EdgePadding"/> DIP clear of each side of the key box.
    /// <para>
    /// A key with a <see cref="KeyDefinition.SubLabel"/> draws a second line under the symbol, so
    /// the symbol is additionally capped so that symbol + word fits the key's height with the same
    /// edge padding: <c>symbolFont + SubLabelFont(baseFont) &lt;= keySize - 2 * EdgePadding</c>.
    /// </para>
    /// </summary>
    /// <param name="key">The key being drawn.</param>
    /// <param name="baseFont">The clamped key font, <c>max(keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize)</c>.</param>
    /// <param name="boxWidth">The key Border's width, <c>keySize * units + (units - 1) * 4</c> for units &gt; 1.</param>
    /// <param name="keySize">The key Border's height, i.e. the keyboard's current key size in DIP.</param>
    /// <returns>The label's FontSize.</returns>
    public static double Resolve(KeyDefinition key, double baseFont, double boxWidth, double keySize)
    {
        if (!SymbolCaptionRule.IsSymbolCaption(key)) return baseFont;

        var font = baseFont * SymbolCaptionRule.Scale;
        var typeface = new Typeface(
            Tokens.FontStandard, FontStyles.Normal, Tokens.FontWeightMedium, FontStretches.Normal);
        var advance = new FormattedText(
            key.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, font, Tokens.BrushTextPrimary, ReferencePixelsPerDip).WidthIncludingTrailingWhitespace;
        var room = boxWidth - 2 * SymbolCaptionRule.EdgePadding;
        if (advance > room) font *= room / advance;

        if (key.SubLabel is not null)
        {
            var stackRoom = keySize - 2 * SymbolCaptionRule.EdgePadding - SubLabelFont(baseFont);
            font = Math.Min(font, stackRoom);
        }

        return font;
    }

    /// <summary>
    /// The two-line caption's word line: <see cref="KeyDefinition.SubLabel"/> at
    /// <c>max(<see cref="SubLabelFontRatio"/> * baseFont, Legibility.MinimumGlyphFontSize)</c>,
    /// Normal weight. <see cref="Resolve"/> reserves the height this line takes.
    /// </summary>
    /// <param name="baseFont">The clamped key font.</param>
    /// <returns>The word line's FontSize.</returns>
    public static double SubLabelFont(double baseFont) =>
        Math.Max(baseFont * SubLabelFontRatio, Legibility.MinimumGlyphFontSize);
}
