namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// T13 fixup-06: the glass backdrop's corner ✕, judged by what the viewer SEES. The ✕ is a
/// <c>DwellArcButton</c> — a 100-DIP disc (2 × <see cref="UIConstants.ButtonRadius"/> = 50 DIP)
/// centred <see cref="UIConstants.BackdropHideButtonOffset"/> from each corner, so the glyph
/// inside must be on screen and centred on the dwell target, which is what this gate checks.
///
/// <para>
/// T13 fixup-06 also measured the layout box reaching 10 DIP past the right and top edges at the
/// then-offset 40 and called it the corner design. The user then chose 53 DIP off the 40/50/53
/// corner sheet (2026-10-13), so the second gate below makes that clipping a failure: the whole
/// 100-DIP disc and its 105-DIP rim halo now lie inside the screen at 2.0 and at 1.0.
/// </para>
///
/// <para>
/// Headless: the window's content is hosted in <see cref="FitHost"/> at the user's 2.0 and the
/// window is never shown, so nothing is painted on the owner's screen. The ink read is
/// <see cref="LiveCheck.InkBoundsInDevicePixels"/>, the same glyph-outline measurement the live
/// check uses; the client frame stands in for the screen because production places the window at
/// the screen origin.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class GlassBackdropHideButtonTests
{
    [StaFact]
    public void TheHideButton_GlyphInkIsOnScreen_AndCentredOnItsTarget()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: 1200, inheritFrom: window);

        // Production's corner layout (OverlayVmBindingCoordinator.ShowBackdropIfEnabled :107-111).
        var dipRight = 1920 - UIConstants.BackdropHideButtonOffset;
        var dipTop = UIConstants.BackdropHideButtonOffset;
        window.DrawHideButton(new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(dipTop)));
        host.Relayout();

        var glyph = FitText.Descendants<TextBlock>(root).Single(t => t.Text == "\u2715");
        var rootOrigin = root.PointToScreen(new Point(0, 0));
        var clientDevice = new Rect(0, 0, 1920 * FitHost.Scale, 1200 * FitHost.Scale);
        var ink = LiveCheck.InkBoundsInDevicePixels(glyph);
        var inkClient = new Rect(
            new Point(ink.Left - rootOrigin.X, ink.Top - rootOrigin.Y),
            new Point(ink.Right - rootOrigin.X, ink.Bottom - rootOrigin.Y));

        Check.WithCustomMessage(
                $"the ✕'s ink {inkClient} (device px, client-relative) leaves the client {clientDevice}; the glyph "
                + "the viewer sees must be on screen.")
            .That(clientDevice.Contains(inkClient)).IsTrue();

        // The centre is the LAYOUT box centre — the live check's read, and the one tied to the dwell
        // target. The ink centre is ~1.5 DIP lower (the ✕ outline sits low in its line box), which is
        // why the containment above is judged on the ink and the centre here on the box.
        var boxCentre = glyph.TranslatePoint(new Point(glyph.ActualWidth / 2, glyph.ActualHeight / 2), root);
        Check.WithCustomMessage(
                $"the ✕'s centre ({boxCentre.X:0.##}, {boxCentre.Y:0.##}) DIP is not on its dwell target "
                + $"({dipRight:0.##}, {dipTop:0.##}).")
            .That(boxCentre.X).IsCloseTo(dipRight, 0.01);
        Check.That(boxCentre.Y).IsCloseTo(dipTop, 0.01);
    }

    /// <summary>
    /// The corner choice (2026-10-13): at <see cref="UIConstants.BackdropHideButtonOffset"/> 53 DIP
    /// the ✕'s 100-DIP dwell disc AND its 105-DIP rim halo lie fully inside the screen — at the
    /// user's 2.0 and at 1.0, where the offset is the same 53 DIP while the screen is the primary
    /// monitor's physical bounds in DIP (1920×1200 at 2.0, the full 3840×2400 at 1.0). The bounds
    /// are measured on the production elements <c>DrawHideButton</c> places, not on a restated
    /// formula, so a change to the disc, the halo or the layout fails here too. At the old 40 the
    /// disc reached 10 DIP past the right and top edges — the clipped corner the live check showed,
    /// and the mutant this gate exists to kill.
    /// </summary>
    [StaTheory]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public void TheHideButton_DiscAndHalo_LieFullyInsideTheScreen(double scale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var screenDip = new Size(screen.Bounds.Width / scale, screen.Bounds.Height / scale);

        var window = new GlassBackdropWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: screenDip.Width, height: screenDip.Height, inheritFrom: window);

        // Production's corner layout (OverlayVmBindingCoordinator.ShowBackdropIfEnabled :107-111).
        var dipRight = screenDip.Width - UIConstants.BackdropHideButtonOffset;
        var dipTop = UIConstants.BackdropHideButtonOffset;
        window.DrawHideButton(new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(dipTop)));
        host.Relayout();

        var button = (DwellArcButton?)typeof(GlassBackdropWindow)
            .GetField("_hideButton", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("GlassBackdropWindow._hideButton is gone or was not drawn; repoint this read.");

        var screenDipRect = new Rect(new Point(0, 0), screenDip);
        var disc = CanvasBounds(button.Background);
        var halo = CanvasBounds(button.RimHalo);
        Check.WithCustomMessage(
                $"at {scale:0.0}×, the ✕'s dwell disc {disc} leaves the screen {screenDipRect} "
                + $"({Math.Min(disc.Left, screenDipRect.Width - disc.Right):0.##} DIP of clearance on the tight axis).")
            .That(screenDipRect.Contains(disc)).IsTrue();
        Check.WithCustomMessage(
                $"at {scale:0.0}×, the ✕'s rim halo {halo} leaves the screen {screenDipRect} "
                + $"({Math.Min(halo.Left, screenDipRect.Width - halo.Right):0.##} DIP of clearance on the tight axis).")
            .That(screenDipRect.Contains(halo)).IsTrue();
    }

    /// <summary>The DIP bounds <c>DwellArcButton</c>'s canvas placement gives an element.</summary>
    private static Rect CanvasBounds(FrameworkElement element) =>
        new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);
}
