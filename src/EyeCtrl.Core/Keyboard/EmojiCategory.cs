namespace EyeCtrl.Core.Keyboard;

public class EmojiCategory
{
    public string Id { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Emojis { get; set; } = new();
}
