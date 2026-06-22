namespace EyeCtrl.Core.Keyboard;

/// <summary>Root object for emoji data loaded from the emoji JSON file.</summary>
public class EmojiData
{
    /// <summary>Emoji categories (e.g., smileys, food, travel).</summary>
    public List<EmojiCategory> Categories { get; set; } = new();
    /// <summary>Base emoji characters that support skin tone modifiers.</summary>
    public HashSet<string> SkinToneBase { get; set; } = new();
}
