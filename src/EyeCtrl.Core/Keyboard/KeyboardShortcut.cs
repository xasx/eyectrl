namespace EyeCtrl.Core.Keyboard;

/// <summary>
/// Splits a keyboard chord string ("Ctrl+Shift+S") into its modifiers and terminal key. One
/// parser for every chord surface — Quick Action payloads and the keyboard layout's
/// "shortcut:&lt;chord&gt;" keys — so the accepted spelling cannot drift between them.
/// </summary>
public static class KeyboardShortcut
{
    /// <summary>
    /// Parses <paramref name="chord"/>. The last '+'-separated token is the key (a single
    /// character, or a name <see cref="KeyNameMapper"/> resolves); every earlier token must name
    /// a modifier ("Ctrl", "Shift", "Alt", "Win" — spelled exactly as the preset tables spell
    /// them). An unknown modifier token is dropped rather than fatal: the key still fires,
    /// unmodified by the token we did not understand.
    /// </summary>
    /// <returns>False when the chord has no key part at all (empty string or trailing '+').</returns>
    public static bool TryParse(string chord, out HashSet<ModifierKey> modifiers, out string key)
    {
        modifiers = new HashSet<ModifierKey>();
        var parts = chord.Split('+');
        key = parts[^1].Trim();

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].Trim() switch
            {
                "Ctrl" => (ModifierKey?)ModifierKey.Ctrl,
                "Shift" => ModifierKey.Shift,
                "Alt" => ModifierKey.Alt,
                "Win" => ModifierKey.Win,
                _ => null
            };
            if (modifier.HasValue)
                modifiers.Add(modifier.Value);
        }

        return key.Length > 0;
    }
}
