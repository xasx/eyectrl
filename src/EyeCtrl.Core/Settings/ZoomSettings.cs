namespace EyeCtrl.Core.Settings;
public class ZoomSettings
{
    public bool Enabled { get; set; } = false;
    public int CircleRadiusPixels { get; set; } = 150;
    public double MagnificationLevel { get; set; } = 2.0;
}
