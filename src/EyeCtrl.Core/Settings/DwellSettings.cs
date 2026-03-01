namespace EyeCtrl.Core.Settings;
public class DwellSettings
{
    public int FixationDetectionMs { get; set; } = 300;
    public int ActivationButtonDwellMs { get; set; } = 700;
    public int ActionMenuDwellMs { get; set; } = 600;
    public int ZoomActionMenuDwellMs { get; set; } = 600;
    public int ToolbarButtonDwellMs { get; set; } = 700;
    public int KeyboardKeyDwellMs { get; set; } = 450;
    public int KeyboardPredictionDwellMs { get; set; } = 500;
    public double FixationRadiusPixels { get; set; } = 50;
}
