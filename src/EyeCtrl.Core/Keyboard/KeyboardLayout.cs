namespace EyeCtrl.Core.Keyboard;

public class KeyboardLayout
{
    public string Name { get; set; } = "";
    public List<List<KeyDefinition>> Rows { get; set; } = new();
}
