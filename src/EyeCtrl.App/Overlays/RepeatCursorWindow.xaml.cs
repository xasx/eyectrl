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
    // Field round 2: derive from the user's cursor-size preference (slightly smaller than the
    // normal circle — "slightly bigger" than the old fixed 14), clamped to a sane band.
    private const double MinRadiusLogical = 20;
    private const double MaxRadiusLogical = 70;
    private const double DotRadiusLogical = 2.5;
    private const double PositionSmoothing = 0.35;   // EMA per update: tames jitter without lag

    private double _fullRadiusLogical = 28;
    private Ellipse? _ring;
    private double _smoothedX, _smoothedY;
    private bool _smoothedSeeded;

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

        var size = (_fullRadiusLogical + DotRadiusLogical) * 2 + 8;
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

    /// <summary>Sizes the ring from the user's cursor-circle preference (slightly smaller).</summary>
    public void Configure(double cursorCircleSizeLogical)
    {
        _fullRadiusLogical = Math.Clamp(cursorCircleSizeLogical / 2 * 0.6, MinRadiusLogical, MaxRadiusLogical);
        var size = (_fullRadiusLogical + DotRadiusLogical) * 2 + 8;
        Width = size;
        Height = size;
    }

    public void Update(RepeatCursorViewModel vm)
    {
        // Light EMA on the ring position: the ring is FEEDBACK, not the click target (the fire
        // uses the armed fixation point), so smoothing here costs nothing and hides raw gaze
        // jitter (field round 2: "gaze very jittery").
        if (!_smoothedSeeded)
        {
            _smoothedX = vm.Position.X;
            _smoothedY = vm.Position.Y;
            _smoothedSeeded = true;
        }
        else
        {
            _smoothedX += (vm.Position.X - _smoothedX) * PositionSmoothing;
            _smoothedY += (vm.Position.Y - _smoothedY) * PositionSmoothing;
        }

        // Canonical form (CursorCircleWindow): physical in → logical window origin out.
        Left = DpiHelper.PhysicalToLogicalX(_smoothedX) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(_smoothedY) - Height / 2;

        // Contract toward the dot: radius interpolates from full to dot as progress goes 0 → 1.
        var radius = _fullRadiusLogical - (_fullRadiusLogical - DotRadiusLogical) * vm.DwellProgress;
        if (_ring is null) return;
        _ring.Width = radius * 2;
        _ring.Height = radius * 2;
        var c = (Canvas)_ring.Parent;
        Canvas.SetLeft(_ring, (c.ActualWidth - radius * 2) / 2);
        Canvas.SetTop(_ring, (c.ActualHeight - radius * 2) / 2);
    }

    public void ShowCursor()
    {
        _smoothedSeeded = false;   // re-seed on every show so it snaps to the current gaze
        Show();
    }
    public void HideCursor() => Hide();
}
