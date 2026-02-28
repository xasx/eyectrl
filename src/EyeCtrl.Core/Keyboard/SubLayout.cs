namespace EyeCtrl.Core.Keyboard;

public class SubLayout
{
    public string Label { get; set; } = "";
    public List<List<KeyDefinition>> Rows { get; set; } = new();
}
