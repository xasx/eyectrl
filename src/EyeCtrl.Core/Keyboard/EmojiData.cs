namespace EyeCtrl.Core.Keyboard;

public class EmojiData
{
    public List<EmojiCategory> Categories { get; set; } = new();
    public HashSet<string> SkinToneBase { get; set; } = new();
}
