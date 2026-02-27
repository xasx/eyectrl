namespace EyeCtrl.Core.Settings;
public class DwellSettings
{
    public int FixationDetectionMs { get; set; } = 200;
    public int ActivationButtonDwellMs { get; set; } = 500;
    public int ActionMenuDwellMs { get; set; } = 400;
    public int ZoomActionMenuDwellMs { get; set; } = 400;
    public int ToolbarButtonDwellMs { get; set; } = 500;
    public int KeyboardKeyDwellMs { get; set; } = 300;
    public int KeyboardPredictionDwellMs { get; set; } = 350;
    public double FixationRadiusPixels { get; set; } = 50;
}
