namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Wave C C1, the window side of the "fired" flash: the cursor ring's one brief weight step and
/// the magnifier rim's (fixup-01: the same bands, only wider; never a colour swap). Which fires reach these methods is <c>FiredFlashCoordinatorTests</c>.
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

    /// <summary>
    /// A DispatcherTimer's priority. It has no public getter, so this reads WPF's private field;
    /// a missing field fails loudly rather than reading a default.
    /// </summary>
    private static DispatcherPriority TimerPriority(DispatcherTimer timer)
    {
        var field = typeof(DispatcherTimer).GetField(
            "_priority", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check.WithCustomMessage("DispatcherTimer._priority is gone; find another way to read the timer's priority.")
            .That(field).IsNotNull();
        return (DispatcherPriority)field!.GetValue(timer)!;
    }

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
    public void TheEndTimer_RunsAtRenderPriority()
    {
        // fixup-01 R5. Never shown, so it can never skip for a missing DWM cloak (r02 G2): the
        // timer exists from the ctor. At Render the end tick queues behind the render the step's
        // Visibility write requested, so a UI stall cannot end the step before a frame drew it.
        // Mutation: build the timer at Normal → RED.
        var window = NewRing();
        try
        {
            Check.WithCustomMessage("The end tick must queue behind the render that draws the step (fixup-01 R5).")
                .That(TimerPriority(window.FiredFlashTimer)).IsEqualTo(DispatcherPriority.Render);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ARestyleWhileTheRingIsIdle_MovesThePeakToTheNewLook()
    {
        // r02 G1: the peak is rebuilt with EVERY UpdateAppearance, not memoised on the first one. A
        // restyle (style, size, thickness, colour) while no flash is live must leave the next flash
        // drawing the NEW ring's peak. Asserted on the overlays' painted properties, which are all a
        // flash shows (FlashFired only toggles their visibility), on a never-shown window so it
        // cannot skip. Mutation: memoise the peak on the first UpdateAppearance → RED.
        var window = NewRing(); // UpdateAppearance(A): the owner's AchromaticReticle 200 / 1.5
        try
        {
            var restyled = new CursorSettings
            {
                RingStyle = CursorRingStyle.WeightedSandwich,
                CircleSize = 150,
                CircleThickness = 3.0,
                CircleOpacity = 0.8,
                CircleColor = "#112233",
            };

            window.UpdateAppearance(restyled);
            ((ICursorCircleOverlay)window).FlashFired(); // hidden: a no-op, the peak stays as painted

            var stack = CursorRingStacks.For(
                restyled.RingStyle, restyled.CircleSize, restyled.CircleThickness, restyled.CircleOpacity,
                restyled.CircleColor, HexOf(Tokens.StrokeHalo), HexOf(Tokens.TextPrimary));
            var peak = FiredFlash.PeakStack(stack, restyled.CircleSize);

            AssertBand(window.FiredFlashOuter, peak.Outer, restyled.CircleSize, "outer");
            AssertBand(window.FiredFlashCore, peak.Core, restyled.CircleSize, "core");
        }
        finally
        {
            window.Close();
        }
    }

    private static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void AssertBand(Shape overlay, RingBand expected, double size, string name)
    {
        Check.WithCustomMessage($"The {name} flash band kept a stale thickness after the restyle.")
            .That(overlay.StrokeThickness).IsCloseTo(expected.Thickness, 1e-9);
        Check.WithCustomMessage($"The {name} flash band kept a stale colour after the restyle.")
            .That(((SolidColorBrush)overlay.Stroke).Color).IsEqualTo((Color)ColorConverter.ConvertFromString(expected.Hex));
        Check.WithCustomMessage($"The {name} flash band kept a stale opacity after the restyle.")
            .That(overlay.Opacity).IsEqualTo(expected.Opacity);
        Check.WithCustomMessage($"The {name} flash band kept a stale size after the restyle.")
            .That(overlay.Width).IsEqualTo(size);
    }

    [StaFact]
    public void AHiddenRing_IgnoresAFire_AndQueuesNothing()
    {
        // Mutation: drop the IsVisible guard → the hidden ring lights → RED.
        var window = NewRing();
        try
        {
            ((ICursorCircleOverlay)window).FlashFired();

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
    public void AVisibleRing_MakesOneWeightStep_ThenReturnsToItsRestLook_AtBothWindowOpacities(bool pinned)
    {
        // The quiet look (window opacity 0.6) and the pinned look (1.0). The step is the ring's own
        // two bands again, in their own colours and opacities, only wider: the dark core 3x, the
        // light band's exposed part 2x. It never writes Window.Opacity and leaves the rest bands
        // exactly as CursorRingStacks painted them. Called THROUGH THE INTERFACE (fixup-01 R4): the
        // seam's default no-op must never win over the window. Mutations: a non-public FlashFired
        // → RED; a colour swap on a flash band → RED; an End that leaves a band visible → RED.
        var window = NewRing();
        try
        {
            ShowCloakedOffScreen(window);
            window.SetPinned(pinned);
            var restOpacity = window.Opacity;
            Check.That(restOpacity).IsEqualTo(pinned ? 1.0 : CursorCircleWindow.FreeLookQuietOpacity);
            var outerRest = Look(window.CircleHalo);
            var coreRest = Look(window.Circle);

            ((ICursorCircleOverlay)window).FlashFired();

            Check.WithCustomMessage("A fire on a visible ring must light the step.")
                .That(window.IsFiredFlashLit).IsTrue();
            Check.That(window.FiredFlashOuter.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(window.FiredFlashCore.Visibility).IsEqualTo(Visibility.Visible);
            Check.WithCustomMessage("The step must not touch Window.Opacity (CursorRingRenderTests' two looks).")
                .That(window.Opacity).IsEqualTo(restOpacity);

            var outerPeak = Look(window.FiredFlashOuter);
            var corePeak = Look(window.FiredFlashCore);
            Check.WithCustomMessage("Polarity-keeping: the outer flash band is the outer band's own colour, never a swap.")
                .That(outerPeak.Stroke).IsEqualTo(outerRest.Stroke);
            Check.WithCustomMessage("Polarity-keeping: the core flash band is the core band's own colour, never a swap.")
                .That(corePeak.Stroke).IsEqualTo(coreRest.Stroke);
            Check.That(outerPeak.Opacity).IsEqualTo(outerRest.Opacity);
            Check.That(corePeak.Opacity).IsEqualTo(coreRest.Opacity);
            Check.WithCustomMessage("The dark core must be 3x as wide at the peak.")
                .That(corePeak.Thickness).IsCloseTo(coreRest.Thickness * 3, 1e-9);
            Check.WithCustomMessage("The light band's exposed part must be 2x as wide at the peak.")
                .That(outerPeak.Thickness - corePeak.Thickness)
                .IsCloseTo((outerRest.Thickness - coreRest.Thickness) * 2, 1e-9);
            Check.That(window.FiredFlashOuter.Width).IsEqualTo(window.CircleHalo.Width);
            Check.That(window.FiredFlashCore.Width).IsEqualTo(window.Circle.Width);

            var canvas = (System.Windows.Controls.Canvas)window.Content;
            Check.WithCustomMessage("The flash bands must paint over the rest bands, outer before core.")
                .That(canvas.Children.IndexOf(window.FiredFlashOuter))
                .IsStrictlyGreaterThan(canvas.Children.IndexOf(window.Circle));
            Check.That(canvas.Children.IndexOf(window.FiredFlashCore))
                .IsStrictlyGreaterThan(canvas.Children.IndexOf(window.FiredFlashOuter));

            Check.That(window.IsFiredFlashEndScheduled).IsTrue();
            Check.WithCustomMessage("The whole flash must end within 150 ms.")
                .That(window.FiredFlashInterval.TotalMilliseconds).IsStrictlyGreaterThan(0).And.IsLessOrEqualThan(150);

            window.EndFiredFlash();

            Check.That(window.IsFiredFlashLit).IsFalse();
            Check.That(window.FiredFlashOuter.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(window.FiredFlashCore.Visibility).IsEqualTo(Visibility.Collapsed);
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

            ((ICursorCircleOverlay)window).FlashFired();
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
            ((ICursorCircleOverlay)window).FlashFired();
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
    public void TheMagnifierRim_MakesOneWeightStep_ThenReturnsToRest_AndItsRimIsNeverWritten()
    {
        // The window is constructed cloaked and never shown: FlashRim needs no visibility (the
        // coordinator calls it at the magnifier's own show). Called through the interface
        // (fixup-01 R4). Mutations: a no-op or non-public FlashRim → RED; a colour swap → RED.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new ManualSelectionOverlayWindow());
        try
        {
            var haloRest = Look(window.RimHalo);
            var rimRest = Look(window.Rim);

            ((IManualSelectionOverlay)window).FlashRim();

            Check.That(window.IsRimFlashLit).IsTrue();
            Check.That(window.RimFlashHalo.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(window.RimFlash.Visibility).IsEqualTo(Visibility.Visible);
            Check.WithCustomMessage("Polarity-keeping: the halo flash is the halo's own colour.")
                .That(Look(window.RimFlashHalo).Stroke).IsEqualTo(haloRest.Stroke);
            Check.WithCustomMessage("Polarity-keeping: the rim flash is the rim's own colour.")
                .That(Look(window.RimFlash).Stroke).IsEqualTo(rimRest.Stroke);
            Check.WithCustomMessage("The rim must be 3x as wide at the peak.")
                .That(window.RimFlash.StrokeThickness).IsCloseTo(rimRest.Thickness * 3, 1e-9);
            Check.WithCustomMessage("The halo's exposed part must be 2x as wide at the peak.")
                .That(window.RimFlashHalo.StrokeThickness - window.RimFlash.StrokeThickness)
                .IsCloseTo((haloRest.Thickness - rimRest.Thickness) * 2, 1e-9);
            Check.That(window.RimFlashInterval.TotalMilliseconds).IsStrictlyGreaterThan(0).And.IsLessOrEqualThan(150);

            window.EndRimFlash();

            Check.That(window.IsRimFlashLit).IsFalse();
            Check.That(window.RimFlashHalo.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(window.RimFlash.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(Look(window.RimHalo)).IsEqualTo(haloRest);
            Check.That(Look(window.Rim)).IsEqualTo(rimRest);
        }
        finally
        {
            window.Close();
        }
    }
}
