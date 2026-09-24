namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;

/// <summary>
/// Thin edge-bar shown WHENEVER the app is paused (a pure function of the paused state — any
/// toolbar mode, any provider), so the user always has pause feedback even when the toolbar —
/// and its resume button — is off-screen. When the toolbar is docked and auto-hidden, the
/// strip additionally marks the "look here to summon" edge. Click-through.
/// </summary>
public partial class PauseIndicatorWindow : OverlayWindow, IPauseIndicatorOverlay
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
                (Left, Top) = OverlayPositioning.PlaceAtEdge(screen, "top", Width, Height);
                break;
            case "bottom":
                Width = screen.Width;
                Height = BarThicknessLogical;
                (Left, Top) = OverlayPositioning.PlaceAtEdge(screen, "bottom", Width, Height);
                break;
            case "left":
                Width = BarThicknessLogical;
                Height = screen.Height;
                (Left, Top) = OverlayPositioning.PlaceAtEdge(screen, "left", Width, Height);
                break;
            case "right":
                Width = BarThicknessLogical;
                Height = screen.Height;
                (Left, Top) = OverlayPositioning.PlaceAtEdge(screen, "right", Width, Height);
                break;
            default:
                Width = screen.Width;
                Height = BarThicknessLogical;
                (Left, Top) = OverlayPositioning.PlaceAtEdge(screen, "top", Width, Height);
                break;
        }
    }
}
