namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;

/// <summary>What <see cref="ProbeRingOnScreenHarness.Run"/> did: nothing (gate closed) or the full run.</summary>
internal enum ProbeRingOnScreenOutcome
{
    /// <summary><c>EYECTRL_UI_TESTS</c> is unset: the gate returned before touching WPF.</summary>
    GateClosed,

    /// <summary>The harness ran on screen and its assertions passed.</summary>
    Completed,
}

/// <summary>
/// R2's on-screen half (ticket requirements a-h): shows a small, non-activating, topmost window
/// hosting the probe's real <see cref="ProbeRingVisual"/>, places the ring at scheduled centres,
/// flushes DWM before every capture, captures through the production GDI path
/// (<see cref="ScreenCaptureService.CaptureRegion"/>, the same <c>CopyFromScreen</c>
/// <c>ScreenCaptureService</c> uses) and judges every frame with
/// <see cref="ProbeRingCaptureAnalysis"/>.
///
/// <para>
/// <b>It is inert without the gate, twice over.</b> <see cref="Run"/> returns
/// <see cref="ProbeRingOnScreenOutcome.GateClosed"/> before constructing anything when
/// <see cref="AppFixture.UiTestsEnabled"/> is false, and the UI test that calls it starts with
/// <see cref="AppFixture.SkipIfDisabled"/>. The headless pin for the harness's own gate is
/// <c>ProbeRingOnScreenGateTests</c>, which drives <see cref="Run"/> with the variable unset and a
/// factory that throws: the gate is proven to sit before window construction, not merely to exist.
/// </para>
///
/// <para>
/// <b>Owner safety (h).</b> The window is ~320 DIP square (ring plus margin), never activates,
/// stays out of the taskbar, hides the OS cursor over itself (as the production probe window does),
/// and is closed in a <c>finally</c>. Nothing simulates input. The reference, A/B and sweep captures
/// together show the window for a few seconds — well inside the ~30 s the ticket allows.
/// </para>
///
/// <para>
/// <b>Why the window is opaque and keyed magenta.</b> A capture has to be judged against a known
/// background; a transparent window would capture the desktop inside the frame and make the
/// positive control meaningless. The key colour never collides with a ring band (every theme's halo
/// is near-black and the core is the owner's colour), and the analysis measures the actual
/// background from each capture rather than trusting the constant.
/// </para>
/// </summary>
internal static class ProbeRingOnScreenHarness
{
    /// <summary>The window's side, in DIPs: the 250-DIP ring plus margin on every side.</summary>
    internal const double WindowDip = 320.0;

    /// <summary>The capture's half-size, in pixels; the 250 px stroke radius plus the scan windows.</summary>
    private const int CaptureRadiusPx = 300;

    private const int CaptureSizePx = 2 * CaptureRadiusPx;

    /// <summary>R2's stated tolerance, the same ±0.05 px R1 asserts at (requirement e).</summary>
    internal const double TolerancePx = 0.05;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>
    /// Runs R2, or returns <see cref="ProbeRingOnScreenOutcome.GateClosed"/> without touching WPF
    /// when UI tests are not opted in.
    /// </summary>
    /// <param name="writeLine">Receives the session report lines.</param>
    /// <param name="initializeDisplayScale">
    /// Sets the process-global <c>DpiHelper</c> to the window's measured scale, the way production
    /// does at startup. Required on the capture path: <see cref="ScreenCaptureService"/> derives the
    /// physical screen size (its clamp bounds) from <c>DpiHelper</c>, so left at the process default
    /// the capture would clamp against a half-size screen and measure the wrong place. The write is
    /// the caller's because the repo's DpiHelper-writer guard requires every file that writes the
    /// scale to declare a non-parallel collection — the UI test class does; this static helper
    /// cannot.
    /// </param>
    /// <param name="windowFactory">
    /// Test seam for the gate proof only; production and the UI test leave it null. A factory that
    /// throws is how <c>ProbeRingOnScreenGateTests</c> proves the gate sits before construction
    /// even when it runs the harness for real.
    /// </param>
    internal static ProbeRingOnScreenOutcome Run(
        Action<string> writeLine,
        Action<double>? initializeDisplayScale = null,
        Func<ProbeRingTestWindow>? windowFactory = null)
    {
        // THE GATE. Nothing below this line may run without an explicit owner opt-in.
        if (!AppFixture.UiTestsEnabled)
            return ProbeRingOnScreenOutcome.GateClosed;

        RunWhenEnabled(
            writeLine ?? throw new ArgumentNullException(nameof(writeLine)),
            initializeDisplayScale ?? throw new ArgumentNullException(nameof(initializeDisplayScale)),
            windowFactory ?? (() => new ProbeRingTestWindow()));
        return ProbeRingOnScreenOutcome.Completed;
    }

    private static void RunWhenEnabled(
        Action<string> writeLine, Action<double> initializeDisplayScale, Func<ProbeRingTestWindow> windowFactory)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        ProbeRingTestWindow? window = null;
        try
        {
            window = windowFactory();
            window.Show();

            // (a) The effective DPI must be 192 or the test fails with this message — never a pass
            // and never a silent skip: on any other scale DWM bitmap-stretching would fake the blur.
            var scale = VisualTreeHelper.GetDpi(window).PixelsPerDip;
            var dpi = ProbeRingCaptureAnalysis.CheckEffectiveDpi(scale);
            writeLine(Invariant($"R2 effective window scale: {scale:0.###}"));
            Check.WithCustomMessage(dpi.Message).That(dpi.IsOwnerScale).IsTrue();

            var ring = new ProbeRingVisual(scale, ProbeRingCaptureScene.OwnerLook(), ProbeRingCaptureScene.LookOpacity);
            window.AttachRing(ring);

            // ScreenCaptureService derives the physical screen size from DpiHelper (its clamping
            // bounds), and production initializes DpiHelper at startup — the test host never does.
            // Left at the process default 1.0 this is invisible in the geometry until the capture,
            // which then clamps against a half-size "screen" and silently measures the wrong place.
            // The caller writes it (the UI test, in the non-parallel WpfApplication collection);
            // the assembly's DpiHelperRestore hook puts the scale back after the test.
            initializeDisplayScale(scale);

            var originPhys = new PhysicalPoint(window.Left * scale, window.Top * scale);
            var halfDip = WindowDip / 2.0;
            var baseCentrePx = new PhysicalPoint(
                originPhys.X + halfDip * scale, originPhys.Y + halfDip * scale);
            writeLine(Invariant(
                $"R2 window at ({window.Left:0.##}, {window.Top:0.##}) DIP; ring centre scheduled at ({baseCentrePx.X:0.##}, {baseCentrePx.Y:0.##}) px; captures are {CaptureSizePx} px square."));

            var halfPx = ProbeRingCaptureScene.RingSizeDip * scale / 2.0;

            var referenceB = CaptureAt(window, ring, baseCentrePx, snap: false);
            var referenceA = CaptureAt(window, ring, baseCentrePx, snap: true);

            var geometry = ProbeRingCaptureAnalysis.RingScanGeometry.For(
                new PhysicalPoint(baseCentrePx.X - referenceB.OriginX, baseCentrePx.Y - referenceB.OriginY),
                halfPx);

            // (c) The positive control on the reference frame, against the same ring rendered
            // offscreen at the same scale and converted through the same weight transform. A capture
            // of the desktop behind an excluded window cannot pass this.
            var expectedBgra = ProbeRingCaptureScene.RenderCapture(
                scale, ProbeRingCaptureScene.OwnerLook(),
                new PhysicalPoint(geometry.ReferenceCentreX, geometry.ReferenceCentreY),
                snap: false, CaptureSizePx);
            var expected = ProbeRingCaptureAnalysis.ToWeights(
                expectedBgra, CaptureSizePx, CaptureSizePx, referenceB.Background);

            var control = ProbeRingCaptureAnalysis.CheckExpectedRing(
                referenceB.Weights, expected,
                new PhysicalPoint(geometry.ReferenceCentreX, geometry.ReferenceCentreY), halfPx);
            writeLine($"R2 positive control: {control.Reason}");
            Check.WithCustomMessage(control.Reason).That(control.Matched).IsTrue();

            // With nothing to round, A and B must draw at the same place (R1's first assertion).
            var referenceOffset = ProbeRingCaptureAnalysis.MeasureShift(referenceA.Weights, referenceB.Weights, geometry);
            writeLine(Invariant(
                $"R2 reference A-B offset at f=0: ({referenceOffset.X:0.0000}, {referenceOffset.Y:0.0000}) px"));
            Check.WithCustomMessage(
                    "With nothing to round, A and B must draw at the same place; measured "
                    + $"({referenceOffset.X:0.0000}, {referenceOffset.Y:0.0000}) px.")
                .That(referenceOffset.X).IsCloseTo(0.0, TolerancePx);
            Check.WithCustomMessage(
                    $"The reference frames' vertical offset must also be 0, measured {referenceOffset.Y:0.0000} px.")
                .That(referenceOffset.Y).IsCloseTo(0.0, TolerancePx);

            // (e) R1's centroid assertions on the hardware path.
            foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
            {
                var movedCentre = new PhysicalPoint(baseCentrePx.X + fraction, baseCentrePx.Y + fraction);
                var b = CaptureAt(window, ring, movedCentre, snap: false);
                var a = CaptureAt(window, ring, movedCentre, snap: true);

                var shiftB = ProbeRingCaptureAnalysis.MeasureShift(b.Weights, referenceB.Weights, geometry);
                var shiftA = ProbeRingCaptureAnalysis.MeasureShift(a.Weights, referenceA.Weights, geometry);
                var expectedA = Math.Round(fraction, MidpointRounding.AwayFromZero);

                writeLine(Invariant(
                    $"R2 hardware f={fraction:0.00}: B=({shiftB.X:0.0000}, {shiftB.Y:0.0000}), A=({shiftA.X:0.0000}, {shiftA.Y:0.0000})"));

                Check.WithCustomMessage(
                        $"Condition B must render fractionally on the hardware path: f = {fraction:0.00} px but "
                        + $"the ring moved ({shiftB.X:0.0000}, {shiftB.Y:0.0000}) px. If either axis is 0 or 1, "
                        + "something snapped the ring to device pixels (m1/m4's shape).")
                    .That(shiftB.X).IsCloseTo(fraction, TolerancePx);
                Check.WithCustomMessage(
                        $"Condition B's vertical move at f = {fraction:0.00} measured {shiftB.Y:0.0000} px.")
                    .That(shiftB.Y).IsCloseTo(fraction, TolerancePx);
                Check.WithCustomMessage(
                        $"Condition A must land on whole device pixels: f = {fraction:0.00} should move "
                        + $"{expectedA:0} px, measured {shiftA.X:0.0000}.")
                    .That(shiftA.X).IsCloseTo(expectedA, TolerancePx);
                Check.WithCustomMessage(
                        $"Condition A's vertical move at f = {fraction:0.00} should be {expectedA:0} px, "
                        + $"measured {shiftA.Y:0.0000}.")
                    .That(shiftA.Y).IsCloseTo(expectedA, TolerancePx);
            }

            // (f) The fine-fraction sweep: a record of how finely the real display path resolves B.
            var rows = new List<ProbeRingCaptureAnalysis.SweepRow>();
            for (var i = 0; i < 20; i++)
            {
                var fraction = i * 0.05;
                var moved = CaptureAt(window, ring,
                    new PhysicalPoint(baseCentrePx.X + fraction, baseCentrePx.Y + fraction), snap: false);
                var shift = ProbeRingCaptureAnalysis.MeasureShift(moved.Weights, referenceB.Weights, geometry);
                rows.Add(new ProbeRingCaptureAnalysis.SweepRow(fraction, shift.X, shift.Y));
                writeLine(Invariant(
                    $"R2 sweep f={fraction:0.00}: measured=({shift.X:0.0000}, {shift.Y:0.0000}), error=({shift.X - fraction:0.0000}, {shift.Y - fraction:0.0000})"));
            }

            var summary = ProbeRingCaptureAnalysis.EvaluateSweep(rows);
            writeLine(Invariant(
                $"R2 sweep summary: max |error| {summary.MaxAbsErrorPx:0.0000} px; resolves B to {summary.ResolvedStepPx:0.00} px. {summary.Note}"));
        }
        finally
        {
            // (h) No path leaves the window on the owner's screen.
            window?.Close();
        }
    }

    /// <summary>
    /// The one place a capture happens: place the ring, present the frame
    /// (<see cref="ProbeRingTestWindow.Present"/>), flush DWM so the capture cannot read the
    /// previous frame (requirement b), then capture through the production GDI path and convert to
    /// the estimator's weight channel.
    ///
    /// <para>
    /// The source order of the flush and the capture is pinned by
    /// <c>ProbeRingOnScreenGateTests</c>: removing the flush must fail a headless test, not quietly
    /// turn every fraction into a zero.
    /// </para>
    /// </summary>
    private static CapturedFrame CaptureAt(ProbeRingTestWindow window, ProbeRingVisual ring, PhysicalPoint centre, bool snap)
    {
        ring.Place(centre, snap);

        // Wait for the frame carrying the new position, then drain DWM's composition queue: without
        // this pair the capture may be the previous frame.
        window.Present();
        DwmFlush();

        var capture = ScreenCaptureService.CaptureRegion(centre, CaptureRadiusPx);
        if (capture is null)
            throw new InvalidOperationException(
                $"ScreenCaptureService.CaptureRegion({centre.X}, {centre.Y}, {CaptureRadiusPx}) returned null; "
                + "there is no screen to capture.");

        // The region is expected unclamped: the window sits well inside the screen. A clamped
        // origin means ScreenCaptureService's screen bounds are wrong — the DpiHelper contract
        // above was dropped — and the analysis would judge a region the ring is not in.
        var expectedOriginX = (int)Math.Round(centre.X) - CaptureRadiusPx;
        var expectedOriginY = (int)Math.Round(centre.Y) - CaptureRadiusPx;
        if (capture.Value.X != expectedOriginX || capture.Value.Y != expectedOriginY)
        {
            throw new InvalidOperationException(
                $"The capture was clamped to ({capture.Value.X}, {capture.Value.Y}) instead of "
                + $"({expectedOriginX}, {expectedOriginY}); ScreenCaptureService's physical screen size is "
                + "wrong. The harness must initialize DpiHelper from the measured window scale.");
        }

        var bgra = ReadBgra(capture.Value.Image, capture.Value.Size);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(bgra, capture.Value.Size, capture.Value.Size);
        var weights = ProbeRingCaptureAnalysis.ToWeights(bgra, capture.Value.Size, capture.Value.Size, background);
        return new CapturedFrame(weights, capture.Value.X, capture.Value.Y, background);
    }

    private static byte[] ReadBgra(BitmapSource source, int sizePx)
    {
        BitmapSource readable = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0.0);

        var stride = sizePx * 4;
        var pixels = new byte[stride * sizePx];
        readable.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static string Invariant(FormattableString message) =>
        FormattableString.Invariant(message);

    /// <summary>One captured frame: the estimator raster, the capture's screen origin, and its measured background.</summary>
    private sealed record CapturedFrame(
        ProbeRaster Weights, int OriginX, int OriginY, (byte R, byte G, byte B) Background);
}

/// <summary>
/// R2's harness window (requirement d): ring-sized, non-activating, topmost, out of the taskbar,
/// never shown by anything but <see cref="ProbeRingOnScreenHarness"/>. It mirrors the probe
/// window's relevant properties — including <c>UseLayoutRounding = false</c> and
/// <c>SnapsToDevicePixels = false</c>, so the B condition really is fractional — and it is not
/// excluded from capture, which the positive control would catch if it were.
/// </summary>
internal sealed class ProbeRingTestWindow : Window
{
    public ProbeRingTestWindow()
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = false;
        Cursor = Cursors.None;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;

        Width = ProbeRingOnScreenHarness.WindowDip;
        Height = ProbeRingOnScreenHarness.WindowDip;
        Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2.0);
        Top = Math.Round((SystemParameters.PrimaryScreenHeight - Height) / 2.0);

        var key = new SolidColorBrush(ProbeRingCaptureScene.KeyColour);
        key.Freeze();
        Background = key;
    }

    /// <summary>
    /// Puts the ring in the window. Called only after the DPI check passed, so the ring's injected
    /// scale is the window's measured one.
    /// </summary>
    internal void AttachRing(ProbeRingVisual ring)
    {
        var scene = new Canvas
        {
            Width = Width,
            Height = Height,
            Background = Background,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
        };
        scene.Children.Add(ring);
        Content = scene;
        UpdateLayout();
    }

    /// <summary>
    /// Waits for the next composition frame — the one that carries the ring's latest placement —
    /// then returns so the caller can flush DWM and capture. Throws if no frame arrives within 5 s;
    /// a window that does not render is a harness fault, not a rendering result.
    /// </summary>
    internal void Present()
    {
        UpdateLayout();

        var frame = new DispatcherFrame();
        var rendered = false;
        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            rendered = true;
            if (onRendering is not null)
                CompositionTarget.Rendering -= onRendering;
            frame.Continue = false;
        };

        CompositionTarget.Rendering += onRendering;
        var timeout = new DispatcherTimer(
            TimeSpan.FromSeconds(5), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
            if (onRendering is not null)
                CompositionTarget.Rendering -= onRendering;
        }

        if (!rendered)
        {
            throw new InvalidOperationException(
                "The probe window produced no composition frame within 5 s; the ring cannot be captured "
                + "as presented. This is a harness fault, not a rendering result.");
        }
    }
}
