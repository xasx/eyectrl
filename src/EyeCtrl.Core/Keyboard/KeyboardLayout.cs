namespace EyeCtrl.Core.Keyboard;

public class KeyboardLayout
{
    public string Name { get; set; } = "";
    public Dictionary<string, SubLayout> SubLayouts { get; set; } = new();
    public string DefaultSubLayout { get; set; } = "letters";
}
