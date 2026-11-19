namespace EyeCtrl.Core.Keyboard;

/// <summary>
/// The "long look" (F4, widened to every key and every suggestion slot by F8): after a key or a
/// prediction slot fires, the same target re-arms only after the gaze has really gone elsewhere, and
/// a deliberate SECOND dwell of <see cref="RepeatDwellUs"/> on it fires it again.
///
/// <para>
/// One timing rule for both surfaces. The keyboard's latched key and the prediction bar's latched
/// slot count ON-TARGET time only, starting after the surface's own post-fire cooldown; an excursion
/// of at most <see cref="RepeatExcursionToleranceUs"/> pauses the clock instead of resetting it.
/// The values live here, once, so the timing study tunes keys and bar with one edit.
/// </para>
///
/// <para>
/// The same class states which ACTIONS may not long-look repeat. The exclusion is a rule on what
/// the key's action does, never a list of labels: a repeat of a page switch flips back and forth
/// (and would undo itself on every second look), and a repeat of the close or move button hides or
/// moves the keyboard out from under the gaze. Ordinary keys — text, Enter, Tab, Space, the
/// modifiers, the Fn-page actions, the emoji cells and the tone swatches — are repeatable.
/// </para>
/// </summary>
public static class KeyboardLongLook
{
    /// <summary>
    /// How long the latched target must be dwelled a second time to fire again. The clock starts
    /// when its dwell MAY start — AFTER the surface's post-activation cooldown — and counts
    /// on-target time only; see <see cref="RepeatExcursionToleranceUs"/>.
    ///
    /// <para>
    /// <b>1000 ms is the owner's choice (18.11, Q4 "B 1.0 s").</b> The measured trade, from the
    /// 18.11 sitting replayed through the shipped pipeline (F9): at 1000 ms the long look keeps
    /// 6 of 6 intended doubles and adds exactly ONE unwanted letter double — a 1050 ms on-key hold
    /// in that sitting — and no unwanted ⌫ / Wort löschen repeat. The owner accepted about one
    /// unwanted double per sitting of that density for a 200 ms shorter felt double (cooldown 400
    /// + 1000 = 1.4 s from the first fire). The margin is thin by construction: the same gesture
    /// at 1050 ms stays ordinary, at 1000 it repeats, and lowering below 1000 also starts repeating
    /// deliberate ⌫ holds (F9 measured the first extra deletion at 900). Do not "simplify" it to a
    /// normal dwell and do not fork it per surface — the keyboard and the prediction bar share this
    /// one value (F8, F12).
    /// </para>
    ///
    /// <para>
    /// History: F4 chose 1200 from the KB7 replay, where the intended double credited 1208 ms and
    /// the longest unintended pause 1087 ms; that 113 ms margin is gone at 1000, deliberately.
    /// </para>
    /// </summary>
    public const long RepeatDwellUs = 1000 * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>
    /// How long the gaze may leave the latched target during its second dwell before the
    /// accumulated progress RESETS rather than pauses. The progress never counts off-target time;
    /// this only decides whether the run survives the excursion. 400 ms is the largest value
    /// tested in the KB7 replay and the only one that lets the intended double's 120 ms glance
    /// pause instead of reset. Excursions beyond it are a real look elsewhere: on the keyboard the
    /// re-arm latch's own 120 ms off-keyboard rule usually ends the second dwell first, and on the
    /// bar the slot's leave rule does the same, so this bound is the outer one for both.
    /// </summary>
    public const long RepeatExcursionToleranceUs = 400 * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>
    /// Whether a key whose <c>SpecialAction</c> (or, for the emoji surface and the edge pair, whose
    /// dwell id) is <paramref name="action"/> may repeat by a second dwell.
    ///
    /// <para>
    /// Excluded, by what the action DOES:
    /// <list type="bullet">
    /// <item><b>page switches</b> — <c>switchSub:*</c> (123, ABC, Fn, #+=, the accents), the emoji
    /// mode entry/exit (<c>switchEmoji</c>, <c>switchText</c>), the emoji page keys
    /// (<c>emojiPage:*</c>) and the category tabs (<c>emojiCategory:*</c>), plus the skin-tone
    /// panel's open/back keys. A second look would flip the page straight back;</item>
    /// <item><b>close</b> and <b>move/dock</b> — the keyboard's edge pair (<c>Keyboard_close</c>,
    /// <c>Keyboard_flip</c>) and any action named close/move/dock/flip. A repeat would hide the
    /// keyboard or move it into the worst-measured region.</item>
    /// </list>
    /// Everything else — a plain text key (empty action), Enter, Tab, Space, the modifiers, the
    /// Fn-page actions, backspace/Wort löschen, the emoji cells and the tone swatches — is
    /// repeatable.
    /// </para>
    /// </summary>
    /// <param name="action">The key's <c>SpecialAction</c>, or its dwell id where the two coincide
    /// (special-action keys mint their id from the action); null/empty is a plain text key.</param>
    /// <returns>True when a long look may re-fire it.</returns>
    public static bool IsRepeatableAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
            return true;

        if (action.StartsWith("switchSub:", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("emojiPage:", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("emojiCategory:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (action.Equals("switchEmoji", StringComparison.OrdinalIgnoreCase)
            || action.Equals("switchText", StringComparison.OrdinalIgnoreCase)
            || action.Equals("skinTone", StringComparison.OrdinalIgnoreCase)
            || action.Equals("skinToneBack", StringComparison.OrdinalIgnoreCase))
            return false;

        // The keyboard's edge pair. They live on the shared edge bar and never enter the key grid
        // today; stated here so a future close/move key cannot silently become repeatable.
        if (action.Equals("Keyboard_close", StringComparison.OrdinalIgnoreCase)
            || action.Equals("Keyboard_flip", StringComparison.OrdinalIgnoreCase))
            return false;

        return !action.StartsWith("close", StringComparison.OrdinalIgnoreCase)
            && !action.StartsWith("move", StringComparison.OrdinalIgnoreCase)
            && !action.StartsWith("dock", StringComparison.OrdinalIgnoreCase)
            && !action.StartsWith("flip", StringComparison.OrdinalIgnoreCase);
    }
}
