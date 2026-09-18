namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>
/// The app-escaping classification is the input the keyboard's dwell floor keys off, so its exact
/// boundary is load-bearing: a key wrongly left out gets shortened by adaptation (the hazard the
/// floor exists for), and a key wrongly included loses the forgiving response it should have. The
/// boundary is deliberate — see <see cref="KeyboardSpecialActions"/> — and the ids these tests feed
/// are the RAW CASED layout strings, because a classifier that only matched lowercase ids would
/// classify nothing in production while these tests stayed green.
/// </summary>
public class KeyboardSpecialActionsTests
{
    [Theory]
    [InlineData("enter")]
    [InlineData("tab")]
    [InlineData("key:Escape")]
    [InlineData("key:Delete")]
    [InlineData("key:Insert")] // overwrite mode: silent, compounding damage a repeat does not undo
    [InlineData("key:F1")]
    [InlineData("key:F12")]
    [InlineData("shortcut:Ctrl+A")]
    [InlineData("shortcut:Ctrl+Z")]
    public void IsAppEscaping_TrueForTheIrreversibleClasses(string keyId) =>
        Check.That(KeyboardSpecialActions.IsAppEscaping(keyId)).IsTrue();

    [Theory]
    [InlineData("ENTER")]
    [InlineData("Tab")]
    [InlineData("KEY:ESCAPE")] // the cased id as the layout carries it, uppercased in full
    [InlineData("key:escape")]
    [InlineData("key:f5")]
    [InlineData("Shortcut:Ctrl+C")]
    public void IsAppEscaping_IsCaseInsensitive(string keyId) =>
        Check.That(KeyboardSpecialActions.IsAppEscaping(keyId)).IsTrue();

    [Theory]
    // Navigation: pressed in runs, and a wrong one is undone by pressing the opposite key once.
    // These stay fully adaptive — exempting them from the forgiving response would tax speed
    // exactly where speed matters most.
    [InlineData("key:ArrowUp")]
    [InlineData("key:ArrowDown")]
    [InlineData("key:ArrowLeft")]
    [InlineData("key:ArrowRight")]
    [InlineData("key:Home")]
    [InlineData("key:End")]
    [InlineData("key:PageUp")]
    [InlineData("key:PageDown")]
    // PrintScreen copies the screen and destroys nothing — unlike Insert, which it is deliberately
    // not bundled with.
    [InlineData("key:PrintScreen")]
    [InlineData("key:SomethingElse")] // an unknown key: action is not a licence to classify it
    // Text composition and correction.
    [InlineData("backspace")]
    [InlineData("deleteWord")] // Ctrl+Backspace: a correction, reachable by gaze like Backspace
    [InlineData("space")]
    // App-internal and reversible.
    [InlineData("switchSub:function")]
    [InlineData("switchSub:letters")]
    [InlineData("switchEmoji")]
    [InlineData("modifier:win")] // toggles a modifier; emits nothing by itself
    [InlineData("modifier:ctrl")]
    [InlineData("toggle:capslock")]
    [InlineData("special_switchText")] // the emoji grid's ABC key
    [InlineData("emoji_0")]
    // The positional fallback and near misses: neither may match the id grammar.
    [InlineData("key_0_0")]
    [InlineData("key")]
    [InlineData("key_")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAppEscaping_FalseForNavigationTextAndInAppKeys(string? keyId) =>
        Check.That(KeyboardSpecialActions.IsAppEscaping(keyId)).IsFalse();

    [Fact]
    public void AppEscapingPolicyFloorMs_Is600_TheChosenPolicyValue()
    {
        // This guard fired when the constant moved from 0 to 600 — that is it working, not a reason
        // to delete it. Its job is to make ANY movement of this value loudly deliberate: re-aim it
        // with the constant, never before it and never around it.
        //
        // 600 was chosen by the user on 2026-09-19 on consequence-asymmetry grounds: a misfired
        // escaping key is the one failure this app cannot undo, while the extra hold is a measured
        // cost that was deliberately accepted ("I'd rather have the guard than the measurement").
        // The counter-argument (unmeasured accidental rate at 450; space/enter 18.7 sigma apart;
        // steadiness is the user's scarce resource) is on record in the constant's doc.
        Check.That(KeyboardSpecialActions.AppEscapingPolicyFloorMs).IsEqualTo(600);
    }
}
