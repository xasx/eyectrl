namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// T13 fixup-06: the glass backdrop's corner ✕, judged by what the viewer SEES. The ✕ is a
/// <c>DwellArcButton</c> — a 100-DIP disc (2 × <see cref="UIConstants.ButtonRadius"/> = 50 DIP)
/// whose layout box therefore reaches 10 DIP past the right and top edges, because production
/// centres it <see cref="UIConstants.BackdropHideButtonOffset"/> (40 DIP) from each corner. That
/// clipping is the corner design, not a defect; the glyph inside must be on screen and centred on
/// the dwell target, which is what this gate checks.
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
                $"the ✕'s ink {inkClient} (device px, client-relative) leaves the client {clientDevice}; the disc "
                + "may be clipped by the corner design, the glyph may not.")
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
}
