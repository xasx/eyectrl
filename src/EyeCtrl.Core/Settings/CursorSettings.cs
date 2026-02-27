namespace EyeCtrl.Core.Settings;
using EyeCtrl.Core.Models;
public class CursorSettings
{
    public GazeFilterMode FilterMode { get; set; } = GazeFilterMode.Smoothed;
    public double SmoothingFactor { get; set; } = 0.5;
    public int SnapRadiusPixels { get; set; } = 30;
    public string CircleColor { get; set; } = "#00FF00";
    public double CircleOpacity { get; set; } = 0.6;
    public double CircleThickness { get; set; } = 2.0;
    public bool CircleHighContrast { get; set; } = false;
}
