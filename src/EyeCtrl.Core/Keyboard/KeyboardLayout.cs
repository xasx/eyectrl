namespace EyeCtrl.Core.Keyboard;

/// <summary>Represents a keyboard layout with named sub-layouts (letters, symbols, etc.).</summary>
public class KeyboardLayout
{
    /// <summary>Layout name (e.g., "QWERTY").</summary>
    public string Name { get; set; } = "";
    /// <summary>Named sub-layouts keyed by identifier (e.g., "letters", "symbols").</summary>
    public Dictionary<string, SubLayout> SubLayouts { get; set; } = new();
    /// <summary>Identifier of the default sub-layout shown when the keyboard opens.</summary>
    public string DefaultSubLayout { get; set; } = "letters";
}
