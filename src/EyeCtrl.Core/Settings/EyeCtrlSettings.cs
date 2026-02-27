namespace EyeCtrl.Core.Settings;
public class EyeCtrlSettings
{
    public CursorSettings Cursor { get; set; } = new();
    public DwellSettings Dwell { get; set; } = new();
    public ZoomSettings Zoom { get; set; } = new();
    public ActionMenuSettings ActionMenu { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public ToolbarSettings Toolbar { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}
