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
}
