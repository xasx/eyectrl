namespace EyeCtrl.Core.Keyboard;

public class KeyDefinition
{
    public string Label { get; set; } = "";
    public string Output { get; set; } = "";
    public string? ShiftOutput { get; set; }
    public double Width { get; set; } = 1.0;
    public string? SpecialAction { get; set; } // "shift", "backspace", "enter", "tab", "space", "symbols", "layout"
}
