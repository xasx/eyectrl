namespace EyeCtrl.Core.Keyboard;

/// <summary>A single key on the on-screen keyboard, with label, output, and layout properties.</summary>
public class KeyDefinition
{
    /// <summary>Display label shown on the key cap.</summary>
    public string Label { get; set; } = "";
    /// <summary>Text output when the key is pressed (normal state).</summary>
    public string Output { get; set; } = "";
    /// <summary>Text output when Shift is active, or null if the key has no shift variant.</summary>
    public string? ShiftOutput { get; set; }
    /// <summary>Relative width of the key (1.0 = standard width).</summary>
    public double Width { get; set; } = 1.0;
    /// <summary>Special action identifier (e.g., "shift", "backspace", "enter", "tab", "space", "symbols", "layout").</summary>
    public string? SpecialAction { get; set; }
    /// <summary>
    /// Screen-reader name for a caption a reader cannot pronounce (e.g. "⌫" → "Rücktaste"). Null
    /// for keys whose label already reads as its name. Layout data, so it is worded in the
    /// layout's language regardless of the UI language.
    /// </summary>
    public string? AccessibleName { get; set; }
    /// <summary>
    /// Optional second caption line, drawn small under <see cref="Label"/> (T14 round 2, B4:
    /// "⌫" over "Wort"). Null for a single-line key. Static: it never changes with Shift or Caps.
    /// </summary>
    public string? SubLabel { get; set; }
}
