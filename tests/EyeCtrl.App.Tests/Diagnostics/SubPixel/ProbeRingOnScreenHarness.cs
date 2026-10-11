namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

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
/// <c>ScreenCaptureService</c> uses) and judges the run with <see cref="ProbeRingOnScreenJudge"/>.
///
/// <para>
/// <b>It only captures; the judgement is pure.</b> Every assertion, and the A/B set and sweep
/// schedule themselves, live in <see cref="ProbeRingOnScreenJudge"/> and
/// <see cref="ProbeRingFrameSchedule"/>, where headless tests drive them with synthetic frames —
/// including the DPI verdict, which is the only guard against a bitmap-stretched capture passing
/// the control (review 01 M1/O15).
/// </para>
///
/// <para>
/// <b>Two coordinate spaces.</b> The ring is placed in window-local pixels and captured in screen
/// pixels; <see cref="ProbeRingFrameSchedule"/> owns that mapping, and the capture guard checks the
/// real region against the schedule's production-derived origin (review 01 B1/B2).
/// </para>
///
/// <para>
/// <b>Measure first (fixup 03).</b> The run captures everything first — the 5 reference repeats,
/// then A/B and sweep rows 3 times each — saves every frame as a PNG with its manifest under
/// <c>%TEMP%\eyectrl-r2-&lt;stamp&gt;\</c>, measures with <see cref="ProbeRingOnScreenJudge.Measure"/>,
/// prints the full per-axis table (<see cref="ProbeRingOnScreenJudge.WriteReport"/>) and only then
/// states a verdict, as one combined failure summary. The thresholds are unchanged.
/// </para>
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
/// <b>Owner safety (h).</b> The window is ~320 DIP square (ring plus margin), never activates
/// (<c>WS_EX_NOACTIVATE</c>, so even a dwell-click cannot take the foreground), stays out of the
/// taskbar and Alt-Tab (<c>WS_EX_TOOLWINDOW</c>), hides the OS cursor over itself, and is closed in
/// a <c>finally</c>. Nothing simulates input. A <see cref="ProbeRingRunWatchdog"/> ends the process
/// at 30 s, so no stall can leave the window behind. The reference, A/B and sweep captures together
/// show the window for a few seconds.
/// </para>
///
/// <para>
/// <b>Why the window is opaque and keyed magenta.</b> A capture has to be judged against a known
/// background; a transparent window would capture the desktop inside the frame and make the
/// positive control meaningless. The key colour never collides with a ring band (every theme's halo
/// is near-black and the core is the owner's colour), and the analysis measures the actual
/// background from each capture rather than trusting the constant.
/// </para>
///
/// <para>
/// <b>On-screen mutant recipes (review 01 M3).</b>
/// <list type="bullet">
/// <item><b>m1 — A never snaps:</b> in <c>ProbeRingVisual.Place</c>, short-circuit the
/// <c>snap</c> argument to <c>false</c> (or pass <c>false</c> from here). Killed by the Judge's
/// condition-A whole-pixel assertion: A then renders like B and measures f where 0/1 is expected.</item>
/// <item><b>Capture exclusion on:</b> set <see cref="ProbeRingTestWindow.ExcludeFromCapture"/> to
/// <c>true</c> — the one-word edit this switch exists for; it applies
/// <c>SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)</c> in
/// <see cref="ProbeRingTestWindow.OnSourceInitialized"/>. Killed by the positive control, which then
/// sees the desktop behind the window instead of the ring.</item>
/// <item><b>m4 — B snaps:</b> <c>ProbeRingVisual.cs:111</c>, its own
/// <c>SnapsToDevicePixels = false</c> → <c>true</c>. Killed by the Judge's condition-B fraction
/// assertion (B measures 0/1/1 instead of f), exactly as R1 measured under RenderTargetBitmap.</item>
/// <item><b>m1′ — inherited snap:</b> delete the probe's own <c>SnapsToDevicePixels = false;</c>
/// (R1 fixup 01 F1). The harness window and scene are hostile by construction
/// (<c>SnapsToDevicePixels = true</c>, <c>UseLayoutRounding = true</c>), so this mutant is visible on
/// the hardware path too: B snaps and the same fraction assertion kills it.</item>
/// </list>
/// </para>
/// </summary>
internal static class ProbeRingOnScreenHarness
{
    /// <summary>How many times the f = 0 reference is captured in a row: the noise floor (fixup 03 item 3).</summary>
    internal const int ReferenceRepeats = 5;

    /// <summary>How many times each table row is captured; the row is judged on the mean of these.</summary>
    internal const int RowRepeats = 3;

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

        // (h)/(M4) Armed before the window can exist; disarmed on every exit path.
        using var watchdog = new ProbeRingRunWatchdog(ProbeRingRunWatchdog.KillOnTimeout);

        ProbeRingTestWindow? window = null;
        try
        {
            window = windowFactory();
            window.Show();

            var scale = VisualTreeHelper.GetDpi(window).PixelsPerDip;

            // (a)/(F4) The DPI verdict speaks first — before the schedule, which throws its own
            // "physical origin must be whole pixels" on a 175 %/225 % display and would bury
            // requirement (a)'s message. The Judge checks the same verdict again; this one is what
            // a non-192 % run actually sees.
            var dpi = ProbeRingCaptureAnalysis.CheckEffectiveDpi(scale);
            writeLine(Invariant($"R2 scale gate: {scale:0.###}"));
            Check.WithCustomMessage(dpi.Message).That(dpi.IsOwnerScale).IsTrue();

            // ScreenCaptureService derives the physical screen size from DpiHelper (its clamping
            // bounds), and production initializes DpiHelper at startup — the test host never does.
            // The caller writes it (the UI test, in the non-parallel WpfApplication collection);
            // the assembly's DpiHelperRestore hook puts the scale back after the test.
            initializeDisplayScale(scale);

            var (screenWidthPx, screenHeightPx) = PhysicalScreenSize();
            writeLine(Invariant(
                $"R2 window at ({window.Left:0.##}, {window.Top:0.##}) DIP; scale {scale:0.###}; captures are {ProbeRingFrameSchedule.CaptureSizePx} px square."));

            // ---- Collect phase (fixup 03 item 1; fixup 04 F2): the capture order is a pure plan —
            // references interleaved, the sweep pass-major — so no two consecutive captures share a
            // placement and every repeat is a fresh present. No measurement runs until the whole run
            // is captured. ----
            var plan = ProbeRingCapturePlan.Build();
            writeLine(Invariant(
                $"R2 time: estimated visible ~{TimeSpan.FromMilliseconds(plan.Count * ProbeRingCapturePlan.EstimatedMillisecondsPerCapture).TotalSeconds:0.0} s ({plan.Count} captures x {ProbeRingCapturePlan.EstimatedMillisecondsPerCapture} ms); the 30 s watchdog is the hard bound."));

            var startedUtc = DateTimeOffset.UtcNow;
            var captureClock = Stopwatch.StartNew();
            var samples = new List<ProbeRingCapturedSample>();
            var currentLook = ProbeRingCaptureScene.OwnerLook();
            var ring = new ProbeRingVisual(scale, currentLook, ProbeRingCaptureScene.LookOpacity);
            window.AttachRing(ring);

            foreach (var step in plan)
            {
                if (step.Look.RingStyle != currentLook.RingStyle)
                {
                    currentLook = step.Look;
                    ring = new ProbeRingVisual(scale, currentLook, ProbeRingCaptureScene.LookOpacity);
                    window.AttachRing(ring);
                }

                samples.Add(CaptureAt(window, ring, scale, step, screenWidthPx, screenHeightPx, captureClock));
            }

            // (h)/(F6) The window and the watchdog are done the moment the last capture is in: the
            // owner's screen is clear and nothing on disk can trip the Kill before the numbers are
            // computed. The finally below still covers the exception paths.
            var visibleTime = captureClock.Elapsed;
            window.Close();
            window = null;
            watchdog.Dispose();

            writeLine(Invariant(
                $"R2 measured visible time: {visibleTime.TotalSeconds:0.0} s (show to close)."));

            var origins = samples
                .Select(sample => (sample.Frame.CaptureOriginX, sample.Frame.CaptureOriginY))
                .Distinct()
                .ToList();
            writeLine(
                $"R2 capture origin (all {samples.Count} frames): "
                + string.Join(" / ", origins.Select(origin => $"({origin.CaptureOriginX}, {origin.CaptureOriginY})")));

            // ---- Measure + print (fixup 03 items 2-4): the full per-axis table with repeats and
            // mirrored halves, before any verdict. ----
            var referenceB = samples.First(sample =>
                sample.Kind == ProbeRingOnScreenJudge.KindReference
                && sample.Condition == ProbeRingOnScreenJudge.ConditionB
                && sample.Fraction == 0.0 && sample.Repeat == 0);
            var centreInCapture = new PhysicalPoint(
                referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
                referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY);
            var expected = ProbeRingCaptureScene.ExpectedWeightsAt(centreInCapture, referenceB.Background, scale);

            var report = ProbeRingOnScreenJudge.Measure(
                ProbeRingOnScreenJudge.BuildInputs(scale, expected, samples));

            ProbeRingOnScreenJudge.WriteReport(report, writeLine);

            // B2's fail-closed guard, after the report: a moving origin would bias every shift and
            // make the table above unreadable as rendering evidence.
            Check.WithCustomMessage(
                    $"The capture origin moved during the run ({string.Join(", ", origins)}); the shift "
                    + "measurement assumes one constant origin for every frame.")
                .That(origins).HasSize(1);

            // (F6) The report is printed before the save: if %TEMP% is full or AV locks a file, the
            // table above is still the run's evidence. A save failure is reported and does not skip
            // the verdict.
            var folder = ProbeRingCaptureArchive.CreateRunFolder(startedUtc);
            try
            {
                ProbeRingCaptureArchive.Save(folder, samples, scale, startedUtc);
                writeLine($"R2 captures saved to {folder}");
            }
            catch (Exception ex)
            {
                writeLine($"R2 capture save FAILED ({folder}): {ex.Message}");
            }

            // ---- Verdict: one combined failure summary (item 1). ----
            ProbeRingOnScreenJudge.Judge(report);
        }
        finally
        {
            // (h) No path leaves the window on the owner's screen.
            window?.Close();
        }
    }

    /// <summary>
    /// The one place a capture happens: schedule the frame, place the ring at its
    /// <b>window-local</b> centre (the coordinate space <see cref="ProbeRingVisual.Place"/> takes —
    /// review 01 B1), present it (<see cref="ProbeRingTestWindow.Present"/>), flush DWM so the
    /// capture cannot read the previous frame (requirement b), then capture at the <b>screen</b>
    /// centre through the production GDI path and convert to the estimator's weight channel.
    ///
    /// <para>
    /// The schedule's origin comes from <c>CaptureRegionMath.Compute</c> — production's truncating
    /// math, not a re-derivation (review 01 B2) — and the real capture region is checked against it,
    /// so a clamped or drifting origin fails here instead of silently biasing every shift.
    /// </para>
    ///
    /// <para>
    /// The source order of the flush and the capture, and the window-local placement, are pinned by
    /// <c>ProbeRingOnScreenGateTests</c>.
    /// </para>
    /// </summary>
    private static ProbeRingCapturedSample CaptureAt(
        ProbeRingTestWindow window, ProbeRingVisual ring, double scale, ProbeRingCaptureStep step,
        int screenWidthPx, int screenHeightPx, Stopwatch captureClock)
    {
        var frame = ProbeRingFrameSchedule.For(
            window.Left, window.Top, scale, step.FractionX, step.FractionY, screenWidthPx, screenHeightPx);
        ring.Place(frame.RingLocalCentrePx, step.Snap);

        // Wait for the frames carrying the new position, then drain DWM's composition queue: without
        // this pair the capture may be the previous frame.
        window.Present();
        DwmFlush();

        var capture = ScreenCaptureService.CaptureRegion(frame.CaptureCentrePx, ProbeRingFrameSchedule.CaptureRadiusPx);
        if (capture is null)
            throw new InvalidOperationException(
                $"ScreenCaptureService.CaptureRegion({frame.CaptureCentrePx.X}, {frame.CaptureCentrePx.Y}, "
                + $"{ProbeRingFrameSchedule.CaptureRadiusPx}) returned null; there is no screen to capture.");

        if (capture.Value.X != frame.CaptureOriginX || capture.Value.Y != frame.CaptureOriginY)
        {
            throw new InvalidOperationException(
                $"The capture started at ({capture.Value.X}, {capture.Value.Y}), not the production math's "
                + $"({frame.CaptureOriginX}, {frame.CaptureOriginY}). Either the region was clamped — the "
                + "caller must initialize DpiHelper from the measured window scale — or the schedule no longer "
                + "matches CaptureRegionMath.Compute.");
        }

        var bgra = ReadBgra(capture.Value.Image, capture.Value.Size);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(bgra, capture.Value.Size, capture.Value.Size);
        var weights = ProbeRingCaptureAnalysis.ToWeights(bgra, capture.Value.Size, capture.Value.Size, background);
        return new ProbeRingCapturedSample(
            step.Kind, step.Condition, step.FractionX, step.Repeat, frame, bgra, capture.Value.Size, weights,
            background, ProbeRingCaptureScene.LookOf(step.Look), captureClock.ElapsedMilliseconds);
    }

    /// <summary>
    /// The primary screen's size in physical pixels, the capture's clamp bounds — <c>DpiHelper</c>
    /// multiplied by <c>SystemParameters</c>, the same derivation <c>ScreenCaptureService</c> makes.
    /// A drift between the two fails the capture-origin check above with a named cause.
    /// </summary>
    private static (int Width, int Height) PhysicalScreenSize() =>
        ((int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth),
         (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight));

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
}

/// <summary>
/// R2's harness window (requirement d): ring-sized, non-activating, topmost, out of the taskbar and
/// Alt-Tab, never shown by anything but <see cref="ProbeRingOnScreenHarness"/>, and not excluded
/// from capture unless the mutant switch is set. Its flags are <b>hostile by construction</b> —
/// <c>SnapsToDevicePixels = true</c> and <c>UseLayoutRounding = true</c>, exactly as R1's scene is
/// (R1 fixup 01, F1) — so the probe's own explicit <c>false</c> values are the only thing keeping
/// condition B fractional on the hardware path too.
/// </summary>
internal sealed class ProbeRingTestWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

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

        // Hostile by construction (see the class doc): the probe's own flags must win.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Width = ProbeRingFrameSchedule.WindowDip;
        Height = ProbeRingFrameSchedule.WindowDip;
        Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2.0);
        Top = Math.Round((SystemParameters.PrimaryScreenHeight - Height) / 2.0);

        var key = new SolidColorBrush(ProbeRingCaptureScene.KeyColour);
        key.Freeze();
        Background = key;
    }

    /// <summary>
    /// The capture-exclusion mutant's one-word switch (review 01 M3): set it to <c>true</c> and the
    /// window registers <c>WDA_EXCLUDEFROMCAPTURE</c>, so <c>CopyFromScreen</c> returns the desktop
    /// behind it and the positive control must fail. False is the shipping value — R2 measures the
    /// real window.
    /// </summary>
    internal bool ExcludeFromCapture { get; set; }

    /// <summary>
    /// Applies the ex-styles that keep the owner's session untouched (review 01 m2):
    /// <c>WS_EX_NOACTIVATE</c> so a gaze dwell-click on the square cannot take the foreground from
    /// the console (<c>ShowActivated = false</c> only covers <c>Show</c>), and
    /// <c>WS_EX_TOOLWINDOW</c> so it is not in Alt-Tab. The production probe window carries both
    /// (<c>SubPixelProbeWindow</c>, pinned by <c>SubPixelProbeWindowTests</c>). Then the mutant
    /// switch, if set.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow;
        SetWindowLong(hwnd, GwlExStyle, style);

        if (ExcludeFromCapture)
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    /// <summary>The window's own extended style, for the headless style pin (needs an HWND).</summary>
    internal int ExtendedStyleForTest() =>
        GetWindowLong(new WindowInteropHelper(this).Handle, GwlExStyle);

    /// <summary>
    /// Puts the ring in the window. Called only after the scale is measured, so the ring's injected
    /// scale is the window's effective one.
    /// </summary>
    internal void AttachRing(ProbeRingVisual ring)
    {
        var scene = new Canvas
        {
            Width = Width,
            Height = Height,
            Background = Background,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        scene.Children.Add(ring);
        Content = scene;
        UpdateLayout();
    }

    /// <summary>
    /// Waits for two composition frames before returning so the caller can flush DWM and capture.
    /// <see cref="CompositionTarget.Rendering"/> fires <i>before</i> its tick is rendered and WPF's
    /// own present is asynchronous, so one tick does not guarantee the placement is on screen
    /// (review 01 m1); the second tick means a render pass carrying the change has completed, and
    /// the caller's <c>DwmFlush</c> then drains presentation. Throws if the frames do not arrive
    /// within 5 s — a window that does not render is a harness fault, not a rendering result.
    /// </summary>
    internal void Present()
    {
        UpdateLayout();

        var frame = new DispatcherFrame();
        var ticks = 0;
        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            ticks++;
            if (ticks < 2) return;

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

        if (ticks < 2)
        {
            throw new InvalidOperationException(
                "The probe window produced fewer than two composition frames within 5 s; the ring cannot be "
                + "captured as presented. This is a harness fault, not a rendering result.");
        }
    }
}
