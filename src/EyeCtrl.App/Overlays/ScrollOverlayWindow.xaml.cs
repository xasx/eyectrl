namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class ScrollOverlayWindow : OverlayWindow, EyeCtrl.App.Services.IScrollOverlaySurface
{
    public ScrollOverlayWindow()
    {
        // F-S1 (scroll start fix): feedback-only and full-screen, so it must be click-through —
        // a layered window without WS_EX_TRANSPARENT hit-tests its painted pixels, and the wheel
        // ScrollWorker injects at the anchor landed on this window (the crosshair's 0.5 alpha) and
        // was dropped. The stop dwell lives on the edge bar; nothing here is interactive.
        IsClickThrough = true;
        InitializeComponent();
    }

    public void Show(ScrollOverlayViewModel vm)
    {
        Place(vm);
        base.Show();
        Topmost = true;
    }

    /// <summary>
    /// The headless half of <see cref="Show(ScrollOverlayViewModel)"/>: covers the primary screen and
    /// places the anchor marks, without <c>Show()</c> — T09's geometry gate builds the overlay through
    /// this seam on an STA thread (the <c>ModesMenuWindow.BuildButtonsAt</c> pattern).
    /// </summary>
    internal void Place(ScrollOverlayViewModel vm)
    {
        DataContext = vm;

        OverlayPositioning.CoverPrimaryScreen(this);
        OverlayCanvas.Width = Width;
        OverlayCanvas.Height = Height;

        var anchor = vm.Anchor;
        double anchorLogX = DpiHelper.PhysicalToLogicalX(anchor.X);
        double anchorLogY = DpiHelper.PhysicalToLogicalY(anchor.Y);

        AnchorCrosshairH.X1 = anchorLogX - 15; AnchorCrosshairH.Y1 = anchorLogY;
        AnchorCrosshairH.X2 = anchorLogX + 15; AnchorCrosshairH.Y2 = anchorLogY;
        AnchorCrosshairV.X1 = anchorLogX; AnchorCrosshairV.Y1 = anchorLogY - 15;
        AnchorCrosshairV.X2 = anchorLogX; AnchorCrosshairV.Y2 = anchorLogY + 15;
        // The cue's halo under-strokes share the lines' geometry (fixup 02).
        AnchorCrosshairHaloH.X1 = AnchorCrosshairH.X1; AnchorCrosshairHaloH.Y1 = AnchorCrosshairH.Y1;
        AnchorCrosshairHaloH.X2 = AnchorCrosshairH.X2; AnchorCrosshairHaloH.Y2 = AnchorCrosshairH.Y2;
        AnchorCrosshairHaloV.X1 = AnchorCrosshairV.X1; AnchorCrosshairHaloV.Y1 = AnchorCrosshairV.Y1;
        AnchorCrosshairHaloV.X2 = AnchorCrosshairV.X2; AnchorCrosshairHaloV.Y2 = AnchorCrosshairV.Y2;

        // C4: the start circle, centred on the anchor. Its diameter is the VM's physical start
        // radius converted back to DIP (2 × StartRadiusDip at any scale, unless the dead zone is wider).
        double startDiameter = 2 * DpiHelper.PhysicalToLogicalX(vm.StartRadiusPx);
        foreach (var ring in new[] { StartCircleHalo, StartCircle })
        {
            ring.Width = startDiameter;
            ring.Height = startDiameter;
            Canvas.SetLeft(ring, anchorLogX - startDiameter / 2);
            Canvas.SetTop(ring, anchorLogY - startDiameter / 2);
        }
        SetStartCircleVisible(!vm.IsArmed);

        Canvas.SetLeft(CursorCircle, anchorLogX - 40);
        Canvas.SetTop(CursorCircle, anchorLogY - 40);
        Canvas.SetLeft(AnchorDot, anchorLogX - 4);
        Canvas.SetTop(AnchorDot, anchorLogY - 4);
        Canvas.SetLeft(VelocityRing, anchorLogX - 40);
        Canvas.SetTop(VelocityRing, anchorLogY - 40);
        Canvas.SetLeft(VelocityRingHalo, anchorLogX - 40);
        Canvas.SetTop(VelocityRingHalo, anchorLogY - 40);
    }

    private void SetStartCircleVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StartCircleHalo.Visibility = visibility;
        StartCircle.Visibility = visibility;
    }

    public void UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude)
    {
        // F2 (fixup 01): the arming cue. Until the eye has visited the dead zone the crosshair is
        // drawn at full opacity and double thickness — "look at the cross to start". Once armed it
        // returns to the shipped 0.5 / 1.
        bool armed = (DataContext as ScrollOverlayViewModel)?.IsArmed ?? true;
        double crosshairOpacity = armed ? 0.5 : 1.0;
        double crosshairThickness = armed ? 1.0 : 2.0;
        AnchorCrosshairH.Opacity = crosshairOpacity;
        AnchorCrosshairV.Opacity = crosshairOpacity;
        AnchorCrosshairH.StrokeThickness = crosshairThickness;
        AnchorCrosshairV.StrokeThickness = crosshairThickness;
        // Fixup 02: the halo under-strokes carry the cue on white pages. They follow the cue's
        // thickness (+2 DIP, the VelocityRingHalo pattern) and exist only while unarmed; the armed
        // look is the shipped one (no halo).
        var haloVisibility = armed ? Visibility.Collapsed : Visibility.Visible;
        AnchorCrosshairHaloH.Visibility = haloVisibility;
        AnchorCrosshairHaloV.Visibility = haloVisibility;
        AnchorCrosshairHaloH.StrokeThickness = crosshairThickness + 2;
        AnchorCrosshairHaloV.StrokeThickness = crosshairThickness + 2;
        // C4: the start circle is the same cue — drawn only until scrolling has started.
        SetStartCircleVisible(!armed);

        double anchorX = Canvas.GetLeft(CursorCircle) + 40;
        double anchorY = Canvas.GetTop(CursorCircle) + 40;
        double dotX = anchorX + dotOffsetX * 24;
        double dotY = anchorY + dotOffsetY * 24;
        Canvas.SetLeft(AnchorDot, dotX - 4);
        Canvas.SetTop(AnchorDot, dotY - 4);

        double thickness = 2 + velMagnitude * 0.2;
        VelocityRing.StrokeThickness = Math.Min(thickness, 12);
        VelocityRingHalo.StrokeThickness = Math.Min(thickness, 12) + 2;
    }
}
