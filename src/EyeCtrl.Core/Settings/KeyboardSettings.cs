namespace EyeCtrl.Core.Settings;
public class KeyboardSettings
{
    public string ActiveLayout { get; set; } = "QWERTY";
    public double KeySizeMultiplier { get; set; } = 1.0;
    public bool PredictionEnabled { get; set; } = true;
    public string LanguagePack { get; set; } = "en";
    public string ScreenEdge { get; set; } = "Bottom";
}
