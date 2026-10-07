namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>
/// The T14 symbol-caption rule (user's pick, 2026-10-07). Exactly the command captions that are
/// entirely symbolic take the x1.4 / semi-bold treatment; every letter, digit, space, ASCII
/// character or punctuation disqualifies a caption, and a character key (no specialAction) is
/// never eligible even when its label is a symbol. The App fit tests measure the real keyboard
/// against the same rule; this class pins the boundary itself, in the fastest suite.
/// </summary>
public class SymbolCaptionRuleTests
{
    private static KeyDefinition Special(string label) => new() { Label = label, SpecialAction = "test:action" };

    [Theory]
    [InlineData("\u21EA")]        // ⇪ Caps
    [InlineData("\u21E7")]        // ⇧ Shift
    [InlineData("\u232B")]        // ⌫ Bksp
    [InlineData("\u232B\u232B")]  // ⌫⌫ DelWd
    [InlineData("\u2334")]        // ⌴ Space
    [InlineData("\u21B5")]        // ↵ Enter
    [InlineData("\u21E5")]        // ⇥ Tab
    [InlineData("\u2702")]        // ✂ Cut
    [InlineData("\u21B6")]        // ↶ Undo
    [InlineData("\u21B7")]        // ↷ Redo
    [InlineData("\u2190")]        // ←
    [InlineData("\u2191")]        // ↑
    [InlineData("\u2192")]        // →
    [InlineData("\u2193")]        // ↓
    [InlineData("\uD83D\uDE00")]  // 😀
    public void IsSymbolCaption_TrueForTheFinalSetSymbolCaptions(string label) =>
        Check.That(SymbolCaptionRule.IsSymbolCaption(Special(label))).IsTrue();

    [Theory]
    [InlineData("Strg")]          // a letter disqualifies even with symbols absent
    [InlineData("Druck")]
    [InlineData("Pos1")]
    [InlineData("Ende")]
    [InlineData("Einfg")]
    [InlineData("Entf")]
    [InlineData("Kopieren")]
    [InlineData("Einf\u00FCgen")]
    [InlineData("Bild\u2191")]    // Bild↑: a symbol inside an ordinary caption stays ordinary
    [InlineData("Bild\u2193")]
    [InlineData("+ Wort")]
    [InlineData("Alles mark.")]
    [InlineData("#+=")]           // all-ASCII punctuation is NOT a symbol caption
    [InlineData("123")]
    [InlineData("ABC")]
    [InlineData("Fn")]
    [InlineData("Esc")]
    [InlineData("\u00E0\u00E9")]  // àé
    [InlineData("F1")]
    [InlineData("Leertaste")]
    [InlineData("\u03A0")]        // Π: an uppercase letter, not a symbol
    [InlineData(" ")]             // a space is U+0020, disqualifying and not a text-less label
    [InlineData("\u232B\u2090")]  // ⌫ₐ: mixing a symbol with a letter is ordinary
    public void IsSymbolCaption_FalseForEveryOrdinaryCaptionShape(string label) =>
        Check.That(SymbolCaptionRule.IsSymbolCaption(Special(label))).IsFalse();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00B0")]        // °
    [InlineData("\u20AC")]        // €
    [InlineData("\u00AB")]        // «
    [InlineData("\uD83D\uDE00")]  // 😀
    [InlineData("\u21E7")]        // ⇧
    public void IsSymbolCaption_FalseWithoutASpecialAction(string label)
    {
        // Rule (a): a character key types its glyph and must look like the letters around it,
        // whatever the glyph. € and « are shipped character keys; 😀 is the emoji SWITCH key
        // with an action, which is why this case pins the null-action half at the same label.
        Check.That(SymbolCaptionRule.IsSymbolCaption(new KeyDefinition { Label = label })).IsFalse();
    }

    [Fact]
    public void IsSymbolCaption_FalseForAnEmptyLabel()
    {
        Check.That(SymbolCaptionRule.IsSymbolCaption(Special(""))).IsFalse();
    }

    [Fact]
    public void TheRuleConstants_AreTheUsersPicks()
    {
        // The x1.4 SemiBold pick and the fit limit's 2 DIP edge padding (the same 2 DIP as A68).
        // Pinned here because both are read by the App's font helper and measured independently by
        // the App fit tests: a moved constant must fail a named test, not only a rendered number.
        Check.That(SymbolCaptionRule.Scale).IsEqualTo(1.4);
        Check.That(SymbolCaptionRule.EdgePadding).IsEqualTo(2.0);
    }
}
