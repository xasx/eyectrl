namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>
/// The long look's repeatability boundary (F8). Owner decision 18.11: EVERY key repeats by a second,
/// longer look — Enter, Tab, Space, the modifiers, the Fn-page actions, the emoji cells — except the
/// page switches and the keyboard's close/move buttons, where a repeat would flip the page straight
/// back or hide/move the keyboard under the gaze.
///
/// <para>
/// The exclusion is stated on the ACTION, never on a key's label or position, so it holds for
/// QWERTZ, QWERTY and AZERTY alike and for a future close/move key.
/// </para>
/// </summary>
public class KeyboardLongLookTests
{
    [Theory]
    [InlineData(null)]                  // a plain text key carries no action
    [InlineData("")]                    // the layout's empty action
    [InlineData("enter")]
    [InlineData("tab")]
    [InlineData("space")]
    [InlineData("backspace")]
    [InlineData("deleteWord")]
    [InlineData("addWord")]
    [InlineData("modifier:shift")]
    [InlineData("modifier:ctrl")]
    [InlineData("modifier:alt")]
    [InlineData("modifier:win")]
    [InlineData("toggle:capslock")]
    [InlineData("key:ArrowUp")]
    [InlineData("key:Escape")]
    [InlineData("key:F5")]
    [InlineData("key:Delete")]
    [InlineData("shortcut:Ctrl+C")]
    [InlineData("setSkinTone:2")]       // a tone swatch sets a tone; it is not a page change
    [InlineData("skin_2")]
    [InlineData("emoji_0")]
    public void EverythingThatIsNotAPageSwitchOrACloseMove_Repeats(string? action) =>
        Check.That(KeyboardLongLook.IsRepeatableAction(action)).IsTrue();

    [Theory]
    [InlineData("switchSub:numbers")]   // 123
    [InlineData("switchSub:letters")]   // ABC
    [InlineData("switchSub:function")]  // Fn
    [InlineData("switchSub:symbols")]   // #+=
    [InlineData("switchSub:accents")]   // the accents page key
    [InlineData("switchEmoji")]         // into the emoji page
    [InlineData("switchText")]          // the emoji page's ABC
    [InlineData("emojiPage:prev")]      // the emoji page keys
    [InlineData("emojiPage:next")]
    [InlineData("emojiCategory:recent")]
    [InlineData("skinTone")]            // opens the tone panel (a page swap)
    [InlineData("skinToneBack")]        // back from it
    [InlineData("Keyboard_close")]      // the keyboard's edge pair
    [InlineData("Keyboard_flip")]
    [InlineData("close")]
    [InlineData("move")]
    [InlineData("dock")]
    [InlineData("flip")]
    public void PageSwitchesAndCloseMove_DoNotRepeat(string action) =>
        Check.That(KeyboardLongLook.IsRepeatableAction(action)).IsFalse();

    [Theory]
    [InlineData("SWITCHSUB:NUMBERS")]
    [InlineData("Switchemoji")]
    [InlineData("EMOJIPAGE:NEXT")]
    [InlineData("KEYBOARD_CLOSE")]
    [InlineData("Keyboard_Flip")]
    public void TheRule_IsCaseInsensitive(string action) =>
        Check.That(KeyboardLongLook.IsRepeatableAction(action)).IsFalse();
}
