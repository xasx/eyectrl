namespace EyeCtrl.Core.Keyboard;

/// <summary>A category of emojis displayed as a tab in the emoji picker.</summary>
public class EmojiCategory
{
    /// <summary>Unique category identifier (e.g., "smileys").</summary>
    public string Id { get; set; } = "";
    /// <summary>Emoji character used as the category tab icon.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Human-readable category label.</summary>
    public string Label { get; set; } = "";
    /// <summary>List of emoji characters in this category.</summary>
    public List<string> Emojis { get; set; } = new();
}
