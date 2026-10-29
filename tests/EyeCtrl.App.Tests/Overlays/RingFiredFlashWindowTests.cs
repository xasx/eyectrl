namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Wave C C1, the window side of the "fired" flash: the cursor ring's one brief luminance step and
/// the magnifier rim's. Which fires reach these methods is <c>FiredFlashCoordinatorTests</c>.
/// <para>
/// No window here is ever visible. Every one is cloaked by DWM before its handle is shown
/// (<see cref="InvisibleTestWindow.PrepareCloaked"/>: the ring writes its own Opacity, so the
/// opacity-0 recipe would be undone), both windows are click-through by construction, and a shown
/// ring is parked at -12000 as well. A test skips rather than show a window DWM could not cloak.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class RingFiredFlashWindowTests
{
    /// <summary>The owner's ring (settings.json, 29.10): AchromaticReticle, 200 DIP, thickness 1.5, opacity 1.</summary>
    private static CursorSettings OwnersRing() => new()
    {
        RingStyle = CursorRingStyle.AchromaticReticle,
        CircleSize = 200,
        CircleThickness = 1.5,
        CircleOpacity = 1.0,
    };

    private static CursorCircleWindow NewRing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new CursorCircleWindow());
        window.UpdateAppearance(OwnersRing());
        return window;
    }

    /// <summary>Shows the ring cloaked and off-screen, or skips when DWM cannot cloak it here.</summary>
    private static void ShowCloakedOffScreen(CursorCircleWindow window)
    {
        if (InvisibleTestWindow.IsCloaked(window) != true)
        {
            window.Close();
            Assert.Skip("DWM cannot cloak a window here; this test never shows an uncloaked window.");
        }

        window.Left = -12000;
        window.Top = -12000;
        window.Show();
    }

    private sealed record BandLook(Color Stroke, double Thickness, double Opacity);

    private static BandLook Look(Shape band) =>
        new(((SolidColorBrush)band.Stroke).Color, band.StrokeThickness, band.Opacity);

    private static Color Lighter(Color a, Color b) =>
        FiredFlash.RelativeLuminance(a) >= FiredFlash.RelativeLuminance(b) ? a : b;

    /// <summary>Pumps the dispatcher for <paramref name="duration"/> of REAL time.</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var wake = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = duration,
        };
        wake.Tick += (_, _) =>
        {
            wake.Stop();
            frame.Continue = false;
        };
        wake.Start();
        Dispatcher.PushFrame(frame);
    }

    [StaFact]
    public void AHiddenRing_IgnoresAFire_AndQueuesNothing()
    {
        // Mutation: drop the IsVisible guard → the hidden ring lights → RED.
        var window = NewRing();
        try
        {
            window.FlashFired();

            Check.WithCustomMessage("A fire while the ring is hidden must not flash it.")
                .That(window.IsFiredFlashLit).IsFalse();
            Check.WithCustomMessage("A hidden ring's fire must not be queued for its reappearance.")
                .That(window.IsFiredFlashEndScheduled).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [StaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVisibleRing_MakesOneStep_ThenReturnsToItsRestLook_AtBothWindowOpacities(bool pinned)
    {
        // The quiet look (window opacity 0.6) and the pinned look (1.0). The step covers the whole
        // stroke in the lighter achromatic token, never writes Window.Opacity, and leaves the two
        // bands exactly as CursorRingStacks painted them. Mutation: a FlashFired that does nothing
        // → RED here; an End that leaves the band visible → RED here.
        var window = NewRing();
        try
        {
            ShowCloakedOffScreen(window);
            window.SetPinned(pinned);
            var restOpacity = window.Opacity;
            Check.That(restOpacity).IsEqualTo(pinned ? 1.0 : CursorCircleWindow.FreeLookQuietOpacity);
            var outerRest = Look(window.CircleHalo);
            var coreRest = Look(window.Circle);

            window.FlashFired();

            Check.WithCustomMessage("A fire on a visible ring must light the step.")
                .That(window.IsFiredFlashLit).IsTrue();
            Check.WithCustomMessage("The step must not touch Window.Opacity (CursorRingRenderTests' two looks).")
                .That(window.Opacity).IsEqualTo(restOpacity);
            Check.WithCustomMessage("The step must cover the widest band, so no band edge shows through it.")
                .That(window.FiredFlashBand.StrokeThickness)
                .IsEqualTo(Math.Max(window.CircleHalo.StrokeThickness, window.Circle.StrokeThickness));
            Check.That(window.FiredFlashBand.Width).IsEqualTo(window.CircleHalo.Width);
            Check.WithCustomMessage("The step is drawn in the lighter of the two achromatic tokens.")
                .That(((SolidColorBrush)window.FiredFlashBand.Stroke).Color)
                .IsEqualTo(Lighter(Tokens.TextPrimary, Tokens.StrokeHalo));
            Check.That(window.FiredFlashBand.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("The flash band must paint over both ring bands.")
                .That(((System.Windows.Controls.Canvas)window.Content).Children.IndexOf(window.FiredFlashBand))
                .IsStrictlyGreaterThan(((System.Windows.Controls.Canvas)window.Content).Children.IndexOf(window.Circle));
            Check.That(window.IsFiredFlashEndScheduled).IsTrue();
            Check.WithCustomMessage("The whole flash must end within 150 ms.")
                .That(window.FiredFlashInterval.TotalMilliseconds).IsStrictlyGreaterThan(0).And.IsLessOrEqualThan(150);

            window.EndFiredFlash();

            Check.That(window.IsFiredFlashLit).IsFalse();
            Check.That(window.IsFiredFlashEndScheduled).IsFalse();
            Check.That(window.Opacity).IsEqualTo(restOpacity);
            Check.WithCustomMessage("The outer band is not back at its rest look.")
                .That(Look(window.CircleHalo)).IsEqualTo(outerRest);
            Check.WithCustomMessage("The core band is not back at its rest look.")
                .That(Look(window.Circle)).IsEqualTo(coreRest);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheStep_EndsByItself()
    {
        // The real DispatcherTimer, pumped for real time: the flash returns to rest on its own.
        // Mutation: never start the end timer → still lit after 400 ms → RED.
        var window = NewRing();
        try
        {
            ShowCloakedOffScreen(window);

            window.FlashFired();
            Check.That(window.IsFiredFlashLit).IsTrue();

            PumpFor(TimeSpan.FromMilliseconds(400));

            Check.WithCustomMessage("The flash did not end by itself.")
                .That(window.IsFiredFlashLit).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void HidingTheRingMidStep_ReturnsItToRest_SoItReappearsAtRest()
    {
        // Mutation: drop the hide-ends-the-flash handler → the ring reappears lit → RED.
        var window = NewRing();
        try
        {
            ShowCloakedOffScreen(window);
            window.FlashFired();
            Check.That(window.IsFiredFlashLit).IsTrue();

            window.Hide();
            Check.That(window.IsFiredFlashLit).IsFalse();

            window.Show();
            Check.WithCustomMessage("The ring must come back at rest, not still flashing.")
                .That(window.IsFiredFlashLit).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheMagnifierRim_MakesOneStep_ThenReturnsToRest_AndItsRimIsNeverWritten()
    {
        // The window is constructed cloaked and never shown: FlashRim needs no visibility (the
        // coordinator calls it at the magnifier's own show). Mutation: a FlashRim that does
        // nothing → RED.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new ManualSelectionOverlayWindow());
        try
        {
            var haloRest = Look(window.RimHalo);
            var rimRest = Look(window.Rim);

            window.FlashRim();

            Check.That(window.IsRimFlashLit).IsTrue();
            Check.WithCustomMessage("The rim step must cover the whole rim (RimHalo's width).")
                .That(window.RimFlash.StrokeThickness).IsEqualTo(window.RimHalo.StrokeThickness);
            Check.That(((SolidColorBrush)window.RimFlash.Stroke).Color)
                .IsEqualTo(Lighter(Tokens.TextPrimary, Tokens.StrokeHalo));
            Check.That(window.RimFlashInterval.TotalMilliseconds).IsStrictlyGreaterThan(0).And.IsLessOrEqualThan(150);

            window.EndRimFlash();

            Check.That(window.IsRimFlashLit).IsFalse();
            Check.That(Look(window.RimHalo)).IsEqualTo(haloRest);
            Check.That(Look(window.Rim)).IsEqualTo(rimRest);
        }
        finally
        {
            window.Close();
        }
    }
}
