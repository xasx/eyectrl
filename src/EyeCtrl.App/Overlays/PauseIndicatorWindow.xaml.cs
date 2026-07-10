namespace EyeCtrl.App.Overlays;

using System.Windows;

/// <summary>
/// Thin edge-bar shown when the toolbar is auto-hidden AND the app is paused, so the user
/// isn't left with no feedback (the toolbar — and its resume button — is off-screen).
/// Sits along the docked edge; doubles as a "look here to summon" hint. Click-through.
/// </summary>
public partial class PauseIndicatorWindow : OverlayWindow
{
    private const double BarThicknessLogical = 8;

    public PauseIndicatorWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Positions the bar along the given screen edge. Call after the window has a handle
    /// (e.g. on first Show). Uses SystemParameters.WorkArea in logical units, matching
    /// ToolbarWindow.SetMode's coordinate space.
    /// </summary>
    public void SetEdge(string edge)
    {
        // SystemParameters.WorkArea excludes the taskbar but its Left/Top may be non-zero (taskbar
        // on top/left). We position at WorkArea origin — acceptable for v1 (the toolbar itself uses
        // the same approximation in ToolbarWindow.SetMode). A non-primary monitor would also need
        // handling; out of scope here (spec Non-Goal: single primary display).
        var screen = SystemParameters.WorkArea;

        // No SizeToContent (removed from XAML) — explicit Width/Height below take effect directly.
        switch (edge.ToLowerInvariant())
        {
            case "top":
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = 0;
                break;
            case "bottom":
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = screen.Height - BarThicknessLogical;
                break;
            case "left":
                Width = BarThicknessLogical;
                Height = screen.Height;
                Left = 0;
                Top = 0;
                break;
            case "right":
                Width = BarThicknessLogical;
                Height = screen.Height;
                Left = screen.Width - BarThicknessLogical;
                Top = 0;
                break;
            default:
                Width = screen.Width;
                Height = BarThicknessLogical;
                Left = 0;
                Top = 0;
                break;
        }
    }
}
