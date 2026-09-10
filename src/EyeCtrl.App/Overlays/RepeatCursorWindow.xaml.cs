namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The contracting locked-mode cursor (design D27): a circle notably smaller than the normal
/// cursor circle that shrinks toward its centre dot as the dwell builds (full size at 0, a dot at
/// 1 — where the action fires and it snaps back). Window-less chrome: a fixed-size transparent
/// window centered on the gaze point, moved per sample.
/// </summary>
public partial class RepeatCursorWindow : OverlayWindow, IRepeatCursorOverlay
{
    private const double FullRadiusLogical = 14;   // ~half the normal cursor circle
    private const double DotRadiusLogical = 2.5;

    private Ellipse? _ring;

    public RepeatCursorWindow()
    {
        IsClickThrough = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;

        var size = (FullRadiusLogical + DotRadiusLogical) * 2 + 8;
        Width = size;
        Height = size;

        var canvas = new Canvas();
        _ring = new Ellipse
        {
            Stroke = Tokens.BrushAccent,
            StrokeThickness = 2.5,
            Fill = Brushes.Transparent,
        };
        canvas.Children.Add(_ring);
        Content = canvas;
    }

    public void Update(RepeatCursorViewModel vm)
    {
        // Canonical form (CursorCircleWindow): physical in → logical window origin out. Written
        // as PhysicalToLogical minus half-size so the units are explicit at a glance (review PR
        // #139 round 3 finding 3 — the previous factored form was algebraically the same but
        // read as if it divided wrongly under scaling).
        Left = DpiHelper.PhysicalToLogicalX(vm.Position.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(vm.Position.Y) - Height / 2;

        // Contract toward the dot: radius interpolates from full to dot as progress goes 0 → 1.
        var radius = FullRadiusLogical - (FullRadiusLogical - DotRadiusLogical) * vm.DwellProgress;
        if (_ring is null) return;
        _ring.Width = radius * 2;
        _ring.Height = radius * 2;
        var c = (Canvas)_ring.Parent;
        Canvas.SetLeft(_ring, (c.ActualWidth - radius * 2) / 2);
        Canvas.SetTop(_ring, (c.ActualHeight - radius * 2) / 2);
    }

    public void ShowCursor() => Show();
    public void HideCursor() => Hide();
}
