namespace EyeCtrl.Core.Keyboard;

/// <summary>
/// Maps a key to the CLASS and id E1 logs. The class is a closed set (<see cref="KeyE1"/>); the
/// id is the layout's own special-action string (<c>backspace</c>, <c>switchSub:numbers</c>,
/// <c>shortcut:Ctrl+C</c>, …), which the window already uses as the key's stable dwell-target id.
/// A plain key's id is <c>-</c>: its position is logged as row/column instead, and its character
/// is never logged.
/// </summary>
public static class KeyboardE1Classifier
{
    /// <summary>The "no id" / "not applicable" marker.</summary>
    public const string None = "-";

    /// <summary>The closed id E1 logs for a modifier toggle (<c>shift</c>, <c>caps</c>, <c>ctrl</c>, <c>alt</c>, <c>win</c>).</summary>
    public static string ModifierKind(ModifierKey modifier) => modifier switch
    {
        ModifierKey.Shift => "shift",
        ModifierKey.CapsLock => "caps",
        ModifierKey.Ctrl => "ctrl",
        ModifierKey.Alt => "alt",
        ModifierKey.Win => "win",
        _ => "other",
    };

    /// <param name="key">The key's definition, or null when it is not in the active layout.</param>
    /// <param name="emojiMode">True while the emoji surface is up: every key there is class <c>emoji</c>.</param>
    public static (string Class, string Action) Classify(KeyDefinition? key, bool emojiMode)
    {
        if (emojiMode)
            return (KeyE1.Emoji, None);
        if (key is null)
            return (KeyE1.Other, None);

        if (!string.IsNullOrEmpty(key.SpecialAction))
        {
            var action = key.SpecialAction;
            var cls = action.ToLowerInvariant() switch
            {
                "space" => KeyE1.Space,
                "backspace" => KeyE1.Backspace,
                "deleteword" => KeyE1.DeleteWord,
                "enter" => KeyE1.Enter,
                "shift" or "modifier:shift" => KeyE1.Shift,
                "toggle:capslock" => KeyE1.Caps,
                "modifier:ctrl" or "modifier:alt" or "modifier:win" or "switchemoji" => KeyE1.Mode,
                var a when a.StartsWith("switchsub:", StringComparison.Ordinal) => KeyE1.SwitchSub,
                _ => KeyE1.Other,
            };
            return (cls, action);
        }

        var output = key.Output;
        if (output.Length == 1)
        {
            if (char.IsLetter(output[0])) return (KeyE1.Letter, None);
            if (char.IsDigit(output[0])) return (KeyE1.Digit, None);
        }
        return (KeyE1.Other, None);
    }
}
