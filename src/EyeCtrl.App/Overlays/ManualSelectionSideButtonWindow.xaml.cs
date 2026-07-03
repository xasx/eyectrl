namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ManualSelectionSideButtonWindow : OverlayWindow
{
    // UI Layout Constants
    private const double ButtonRadius = 50; // Radius of ManualSelection side buttons in pixels
    private const int ButtonMargin = 10; // Margin around buttons for window sizing

    private readonly Dictionary<string, DwellArcButton> _buttonVisuals = new();

    public ManualSelectionSideButtonWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void ShowAt(PhysicalPoint closeCenter, PhysicalPoint recaptureCenter)
    {
        _buttonVisuals.Clear();
        ButtonCanvas.Children.Clear();

        // Convert physical → DIP at the boundary
        var closeDipX = DpiHelper.PhysicalToLogicalX(closeCenter.X);
        var closeDipY = DpiHelper.PhysicalToLogicalY(closeCenter.Y);
        var recapDipX = DpiHelper.PhysicalToLogicalX(recaptureCenter.X);
        var recapDipY = DpiHelper.PhysicalToLogicalY(recaptureCenter.Y);

        var minX = Math.Min(closeDipX, recapDipX) - ButtonRadius - ButtonMargin;
        var minY = Math.Min(closeDipY, recapDipY) - ButtonRadius - ButtonMargin;
        var maxX = Math.Max(closeDipX, recapDipX) + ButtonRadius + ButtonMargin;
        var maxY = Math.Max(closeDipY, recapDipY) + ButtonRadius + ButtonMargin;

        Width = maxX - minX;
        Height = maxY - minY;
        Left = minX;
        Top = minY;

        DrawButton("ManualSelection_close", "\u2715", closeDipX - minX, closeDipY - minY);
        DrawButton("ManualSelection_recapture", "\u21BB", recapDipX - minX, recapDipY - minY);

        Show();
    }

    private void DrawButton(string id, string symbol, double cx, double cy)
    {
        var btn = DwellArcButton.Create(new Point(cx, cy), ButtonRadius, symbol);
        btn.AddTo(ButtonCanvas);
        _buttonVisuals[id] = btn;
    }

    public void UpdateProgress(string buttonId, double progress)
    {
        if (_buttonVisuals.TryGetValue(buttonId, out var btn))
            btn.UpdateProgress(progress);
    }
}
