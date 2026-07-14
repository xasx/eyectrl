namespace EyeCtrl.Core.Keyboard;

/// <summary>
/// Maps a special-key name (e.g. "Enter", "F5", "Left") to a virtual-key code.
/// Used by the QuickAction chord parser to resolve named keys in shortcuts like "Ctrl+Enter".
/// </summary>
public static class KeyNameMapper
{
    /// <summary>
    /// Attempts to resolve a key name to its Win32 virtual-key code.
    /// </summary>
    /// <param name="keyName">The case-insensitive key name (e.g. "Enter", "f5", "Left").</param>
    /// <param name="vk">Receives the virtual-key code on success, or 0 on failure.</param>
    /// <param name="isExtended">Receives whether the key requires the KEYEVENTF_EXTENDEDKEY flag
    /// (navigation, edit, and arrow keys); always false for non-extended keys and on failure.</param>
    /// <returns><c>true</c> if the name was recognised; <c>false</c> otherwise.</returns>
    public static bool TryMap(string keyName, out ushort vk, out bool isExtended)
    {
        var (extendedKeys, vk32) = keyName.ToLowerInvariant() switch
        {
            "escape" or "esc" => (false, 0x1B),
            "enter" or "return" => (false, 0x0D),
            "tab" => (false, 0x09),
            "space" => (false, 0x20),
            "backspace" or "back" => (false, 0x08),
            "insert" => (true, 0x2D),
            "delete" or "del" => (true, 0x2E),
            "home" => (true, 0x24),
            "end" => (true, 0x23),
            "pageup" or "pgup" => (true, 0x21),
            "pagedown" or "pgdn" => (true, 0x22),
            "arrowleft" or "left" => (true, 0x25),
            "arrowright" or "right" => (true, 0x27),
            "arrowup" or "up" => (true, 0x26),
            "arrowdown" or "down" => (true, 0x28),
            "printscreen" or "prtsc" => (false, 0x2C),
            "f1" => (false, 0x70),  "f2" => (false, 0x71),  "f3" => (false, 0x72),
            "f4" => (false, 0x73),  "f5" => (false, 0x74),  "f6" => (false, 0x75),
            "f7" => (false, 0x76),  "f8" => (false, 0x77),  "f9" => (false, 0x78),
            "f10" => (false, 0x79), "f11" => (false, 0x7A), "f12" => (false, 0x7B),
            _ => (false, 0)
        };
        vk = (ushort)vk32;
        isExtended = extendedKeys;
        return vk != 0;
    }
}
