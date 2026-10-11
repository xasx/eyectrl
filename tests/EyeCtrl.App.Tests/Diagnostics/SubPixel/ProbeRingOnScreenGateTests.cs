namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's headless pins on the on-screen half — the tests that keep it inert and disciplined without
/// ever running it.
///
/// <list type="number">
/// <item>The gate: with <c>EYECTRL_UI_TESTS</c> unset, <see cref="ProbeRingOnScreenHarness.Run"/>
/// must return before constructing a window. The window factory injected here throws if it is ever
/// called, so removing the gate turns this test red without ever creating a real window — the
/// mutation can be proven headlessly.</item>
/// <item>The window's ex-styles (review 01 m2): <c>WS_EX_NOACTIVATE</c> so a dwell-click cannot
/// steal the foreground and <c>WS_EX_TOOLWINDOW</c> so it is not in Alt-Tab. Pinned on a real HWND
/// from <c>EnsureHandle</c>, never a shown window.</item>
/// <item>The source pins: <c>DwmFlush()</c> before the capture, the ring placed at its
/// <b>window-local</b> centre (B1), the watchdog armed before the window (M4), and the on-screen
/// test's <see cref="AppFixture.SkipIfDisabled"/> before the harness run. All are orderings a
/// future edit can invert with everything else still compiling and the headless suite still
/// green.</item>
/// </list>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingOnScreenGateTests
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [Fact]
    public void WithTheUiVariableUnset_TheHarnessAttemptsNoWindow()
    {
        var saved = Environment.GetEnvironmentVariable("EYECTRL_UI_TESTS");
        try
        {
            Environment.SetEnvironmentVariable("EYECTRL_UI_TESTS", null);

            var windowAttempts = 0;
            var outcome = ProbeRingOnScreenHarness.Run(
                _ => { },
                initializeDisplayScale: _ => { },
                windowFactory: () =>
                {
                    windowAttempts++;
                    throw new InvalidOperationException(
                        "The harness tried to construct a window with EYECTRL_UI_TESTS unset.");
                });

            Check.WithCustomMessage(
                    "With EYECTRL_UI_TESTS unset the harness must return GateClosed before constructing "
                    + "anything. This is the pin that the on-screen half is inert by default; deleting the "
                    + "gate turns this test red.")
                .That(outcome).IsEqualTo(ProbeRingOnScreenOutcome.GateClosed);
            Check.WithCustomMessage(
                    "The window factory must never be invoked with EYECTRL_UI_TESTS unset — an attempt means "
                    + "the gate was bypassed or removed, and on a real desktop that attempt would put a "
                    + "window on the owner's screen.")
                .That(windowAttempts).IsEqualTo(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EYECTRL_UI_TESTS", saved);
        }
    }

    [StaFact]
    public void TheHarnessWindow_IsNoActivateAndToolWindow()
    {
        // Review 01 m2: ShowActivated only covers Show; without WS_EX_NOACTIVATE a gaze dwell-click
        // on the square still takes the foreground from the console, and without WS_EX_TOOLWINDOW
        // the window shows in Alt-Tab. The window gets a real HWND from EnsureHandle and is NEVER
        // shown, exactly as SubPixelProbeWindowTests pins the production probe's styles.
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new ProbeRingTestWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check.That(hwnd).IsNotEqualTo(IntPtr.Zero);

            var style = GetWindowLong(hwnd, GwlExStyle);
            Check.WithCustomMessage(
                    "WS_EX_NOACTIVATE must be set: the owner runs EyeCtrl while R2 runs, and a dwell-click "
                    + "on the square must not steal focus.")
                .That((style & WsExNoActivate) != 0).IsTrue();
            Check.WithCustomMessage("WS_EX_TOOLWINDOW must be set (not in Alt-Tab).")
                .That((style & WsExToolWindow) != 0).IsTrue();

            // F5 (H): the window and its scene are hostile by construction, or the inherited-flag
            // mutant is invisible on the hardware path (review 02 F5). The probe's own flags must
            // still win, which is what keeps condition B fractional.
            window.AttachRing(new ProbeRingVisual(
                2.0, ProbeRingCaptureScene.OwnerLook(), ProbeRingCaptureScene.LookOpacity));

            Check.WithCustomMessage(
                    "The harness window must be hostile by construction (SnapsToDevicePixels and "
                    + "UseLayoutRounding), or the inherited-flag mutant cannot be seen on the hardware path.")
                .That(window.UseLayoutRounding).IsTrue();
            Check.That(window.SnapsToDevicePixels).IsTrue();

            var scene = (Canvas)window.Content;
            Check.WithCustomMessage("The scene canvas must be hostile too.").That(scene.UseLayoutRounding).IsTrue();
            Check.That(scene.SnapsToDevicePixels).IsTrue();

            var ring = (ProbeRingVisual)scene.Children[0];
            Check.WithCustomMessage(
                    "The probe's own explicit false values must still win over the hostile parent, or "
                    + "condition B is no longer fractional.")
                .That(ring.SnapsToDevicePixels).IsFalse();
            Check.That(ring.UseLayoutRounding).IsFalse();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Fixup 07 item 2: the AttachLook helper's <b>body</b> is exercised headlessly — the window is
    /// constructed and never shown. The helper must build the visual from the look it is given,
    /// attach it, and return both: a helper that built <c>OwnerLook()</c> for a solid call (the
    /// silent D2 mislabel) or skipped the attach fails here.
    /// </summary>
    [StaFact]
    public void AttachLook_BuildsTheVisualFromTheGivenLook_AndAttachesIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new ProbeRingTestWindow();
        try
        {
            var (solidRing, solidLook) = ProbeRingOnScreenHarness.AttachLook(
                window, 2.0, ProbeRingCaptureScene.SolidLook());
            Check.That(solidLook.RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
            Check.WithCustomMessage("The window must hold the visual the helper returned.")
                .That(((Canvas)window.Content).Children[0]).IsSameReferenceAs(solidRing);
            Check.WithCustomMessage(
                    "The attached solid visual's strokes must equal a directly built SolidLook visual.")
                .That(StrokeSignature(solidRing)).IsEqualTo(StrokeSignature(
                    new ProbeRingVisual(2.0, ProbeRingCaptureScene.SolidLook(), ProbeRingCaptureScene.LookOpacity)));

            var (ownerRing, ownerLook) = ProbeRingOnScreenHarness.AttachLook(
                window, 2.0, ProbeRingCaptureScene.OwnerLook());
            Check.That(ownerLook.RingStyle).IsEqualTo(CursorRingStyle.BiTonalSegments);
            Check.WithCustomMessage("The attach must replace the window's visual with the new one.")
                .That(((Canvas)window.Content).Children[0]).IsSameReferenceAs(ownerRing);
            Check.WithCustomMessage(
                    "The attached owner visual's strokes must equal a directly built OwnerLook visual.")
                .That(StrokeSignature(ownerRing)).IsEqualTo(StrokeSignature(
                    new ProbeRingVisual(2.0, ProbeRingCaptureScene.OwnerLook(), ProbeRingCaptureScene.LookOpacity)));

            Check.WithCustomMessage("The two looks must render differently, or the comparisons prove nothing.")
                .That(StrokeSignature(ownerRing)).IsNotEqualTo(StrokeSignature(solidRing));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The painted bands of a probe visual, as one comparable string.</summary>
    private static string StrokeSignature(ProbeRingVisual ring)
    {
        static string Band(Ellipse band) =>
            $"{((SolidColorBrush)band.Stroke).Color}|{band.StrokeThickness:0.###}|{band.Opacity:0.###}|"
            + $"{string.Join(",", band.StrokeDashArray.Cast<double>().Select(
                value => value.ToString("0.###", CultureInfo.InvariantCulture)))}|{band.StrokeDashOffset:0.###}";

        return $"halo={Band(ring.CircleHalo)};core={Band(ring.Circle)}";
    }

    [Fact]
    public void TheHarnessSource_FlushesBeforeTheCapture_AndTheUiTestIsGatedFirst()
    {
        var root = SourceScanner.RepoRoot().FullName;

        var harness = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            root, "tests", "EyeCtrl.App.Tests", "Diagnostics", "SubPixel", "ProbeRingOnScreenHarness.cs")));

        const string captureMarker = "private static ProbeRingCapturedSample CaptureAt(";
        var captureStart = harness.IndexOf(captureMarker, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "ProbeRingOnScreenHarness no longer declares CaptureAt(...), the one method that captures "
                + "the screen. Repoint this pin at wherever the capture now happens.")
            .That(captureStart).IsStrictlyGreaterThan(-1);

        var captureEnd = harness.IndexOf("\n    }", captureStart, StringComparison.Ordinal);
        Check.WithCustomMessage("The CaptureAt body is unterminated.").That(captureEnd).IsStrictlyGreaterThan(captureStart);

        var captureBody = harness[captureStart..captureEnd];
        var flush = captureBody.IndexOf("DwmFlush()", StringComparison.Ordinal);
        var capture = captureBody.IndexOf("ScreenCaptureService.CaptureRegion", StringComparison.Ordinal);

        Check.WithCustomMessage(
                "CaptureAt no longer calls DwmFlush(). Without the flush the capture can read the frame "
                + "before the ring moved — the previous frame — and every fraction would measure as 0. "
                + "Body read:\n" + captureBody)
            .That(flush).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "CaptureAt no longer captures through ScreenCaptureService.CaptureRegion, the production "
                + "GDI CopyFromScreen path R2 must measure. Body read:\n" + captureBody)
            .That(capture).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "DwmFlush() must run BEFORE the capture in CaptureAt; a flush after CopyFromScreen cannot "
                + "make the captured frame current. Body read:\n" + captureBody)
            .That(flush).IsStrictlyLessThan(capture);

        // B1: Place takes window-local pixels. Passing the frame's screen centre (the B1 blocker)
        // compiles and passes every other check, so only this pin keeps the two spaces apart here;
        // ProbeRingFrameScheduleTests pins their values and the ring-inside-the-window property.
        Check.WithCustomMessage(
                "CaptureAt no longer places the ring at frame.RingLocalCentrePx — the window-local centre "
                + "Place takes. Passing a screen-pixel centre draws the ring outside the window and the "
                + "control fails on a display that works (review 01 B1). Body read:\n" + captureBody)
            .That(captureBody.IndexOf("ring.Place(frame.RingLocalCentrePx", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);

        // M4: the whole-run cap must be armed before the window can exist.
        var watchdog = harness.IndexOf("new ProbeRingRunWatchdog(", StringComparison.Ordinal);
        var factory = harness.IndexOf("window = windowFactory();", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "ProbeRingOnScreenHarness no longer arms a ProbeRingRunWatchdog: a stalled run would leave "
                + "its topmost window on the owner's screen with no bound (review 01 M4).")
            .That(watchdog).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The window factory call is missing; repoint this pin.")
            .That(factory).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The watchdog must be armed BEFORE the window is constructed, or a factory or Show() stall "
                + "is unbounded.")
            .That(watchdog).IsStrictlyLessThan(factory);

        // F2: the exact arming line, not just "a watchdog appears somewhere". W1 (any other action)
        // and W2 (the using dropped, so a passing run is killed 30 s later) both fail this.
        Check.WithCustomMessage(
                "The watchdog must be armed as `using var watchdog = new ProbeRingRunWatchdog("
                + "ProbeRingRunWatchdog.KillOnTimeout)` — dropping the using leaks a kill into a passing "
                + "run, and any other action re-opens review 01's M4.")
            .That(harness.IndexOf(
                "using var watchdog = new ProbeRingRunWatchdog(ProbeRingRunWatchdog.KillOnTimeout)",
                StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // F2: and the action must actually kill. FailFast goes through WER, which this machine's
        // LocalDumps key can delay or dialog before the process dies (fixup 02 decision).
        var watchdogSource = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            root, "tests", "EyeCtrl.App.Tests", "Diagnostics", "SubPixel", "ProbeRingRunWatchdog.cs")));
        const string killMarker = "internal static void KillOnTimeout()";
        var killStart = watchdogSource.IndexOf(killMarker, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "ProbeRingRunWatchdog no longer declares KillOnTimeout(). Repoint this pin.")
            .That(killStart).IsStrictlyGreaterThan(-1);

        var killBody = watchdogSource[killStart..Math.Min(watchdogSource.Length, killStart + 400)];
        Check.WithCustomMessage(
                "KillOnTimeout must kill the process: a no-op action leaves the topmost window on the "
                + "owner's screen forever (W4). Body read:\n" + killBody)
            .That(killBody).Contains("Process.GetCurrentProcess().Kill()");
        Check.WithCustomMessage(
                "Environment.FailFast must not come back: WER may write a dump (this box has LocalDumps) or "
                + "show a dialog before the process dies, and a dialog is a stuck surface for a gaze-only "
                + "owner.")
            .That(watchdogSource.IndexOf("Environment.FailFast(", StringComparison.Ordinal)).IsEqualTo(-1);

        // F4: the DPI verdict must speak before the schedule can throw on a 175 %/225 % display.
        var dpiGate = harness.IndexOf("CheckEffectiveDpi(scale)", StringComparison.Ordinal);
        var firstCapture = harness.IndexOf("CaptureAt(", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The harness must call CheckEffectiveDpi right after Show(), before the first capture: at "
                + "175 %/225 % the schedule's whole-pixel-origin throw would otherwise bury requirement (a).")
            .That(dpiGate).IsStrictlyGreaterThan(-1);
        Check.That(firstCapture).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("CheckEffectiveDpi must run before the first CaptureAt call.")
            .That(dpiGate).IsStrictlyLessThan(firstCapture);

        // F5 (m1): Present must wait for two composition frames.
        const string presentMarker = "internal void Present()";
        var presentStart = harness.IndexOf(presentMarker, StringComparison.Ordinal);
        Check.WithCustomMessage("ProbeRingTestWindow no longer declares Present().").That(presentStart).IsStrictlyGreaterThan(-1);
        var presentBody = harness[presentStart..harness.IndexOf("\n    }", presentStart, StringComparison.Ordinal)];
        Check.WithCustomMessage(
                "Present() must wait for two composition frames (review 01 m1): one tick fires before its "
                + "render is presented, so the capture could be the previous placement. Body read:\n" + presentBody)
            .That(presentBody).Contains("ticks < 2");

        // Fixup 03 item 5: the harness must save its captures through the archive and print where.
        Check.WithCustomMessage(
                "ProbeRingOnScreenHarness no longer saves its captures through ProbeRingCaptureArchive.Save: "
                + "a failed run's frames would be lost and no later diagnosis would be possible without "
                + "another on-screen run.")
            .That(harness.IndexOf("ProbeRingCaptureArchive.Save(", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must print the capture folder path for the session record.")
            .That(harness.IndexOf("R2 captures saved to", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 04 F6: print the table before saving, close the window (and disarm the watchdog)
        // right after the last capture, and wrap the save so a disk failure cannot skip the table or
        // the verdict.
        var reportAt = harness.IndexOf("ProbeRingOnScreenJudge.WriteReport(", StringComparison.Ordinal);
        var saveAt = harness.IndexOf("ProbeRingCaptureArchive.Save(", StringComparison.Ordinal);
        Check.WithCustomMessage("The harness must print the report.")
            .That(reportAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must save the archive.")
            .That(saveAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The archive must be saved BEFORE the measurement (fixup 09 F2, review 07): the judged "
                + "path is deliberately uncaught and scans 21× as many windows, so a throw in Measure must "
                + "not lose the run's evidence.")
            .That(saveAt).IsStrictlyLessThan(reportAt);

        var earlyClose = harness.IndexOf("window.Close();", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The window must be closed (and the watchdog disarmed) right after the last capture, before "
                + "Save/Measure — a slow disk must never hold the window on screen into the Kill.")
            .That(earlyClose).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The early close must come before the save.")
            .That(earlyClose).IsStrictlyLessThan(saveAt);
        Check.WithCustomMessage(
                "The save must be wrapped: a failure is logged and the verdict still runs.")
            .That(harness.Contains("catch (Exception ex)") && harness.Contains("R2 capture save FAILED")).IsTrue();

        Check.WithCustomMessage("The harness must print the estimated visible time.")
            .That(harness.IndexOf("R2 time: estimated visible", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must print the measured visible time.")
            .That(harness.IndexOf("R2 measured visible time:", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 05 P2: the harness must follow the plan, not a reordered copy of it. Each of these
        // strings is the one call the corresponding mutant rewrites (H1: X for Y; H2: the look
        // switch ignored; H3: the plan reordered).
        Check.WithCustomMessage(
                "The harness must capture in the plan's order — `foreach (var step in plan)` — or the "
                + "references go adjacent and the noise floor degenerates again (H3).")
            .That(harness.IndexOf("foreach (var step in plan)", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "CaptureAt must schedule both fractions from the step: `step.FractionX, step.FractionY` "
                + "(H1 passes X for Y).")
            .That(harness.IndexOf("step.FractionX, step.FractionY", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "CaptureAt must place with the step's snap: `ring.Place(frame.RingLocalCentrePx, step.Snap)`.")
            .That(harness.IndexOf("ring.Place(frame.RingLocalCentrePx, step.Snap)", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The harness must guard that the attached look matches the step's (H2: the look switch "
                + "silently ignored, drawing D2 with the wrong look and mislabelling the manifest).")
            .That(harness.IndexOf("currentLook.RingStyle", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The look switch itself must be present — `if (step.Look.RingStyle != currentLook.RingStyle)` "
                + "and the re-attach inside it (H2).")
            .That(harness.IndexOf("if (step.Look.RingStyle != currentLook.RingStyle)", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "CaptureAt must record the look actually attached — `LookOf(currentLook)` — not the "
                + "step's look (fixup 05 P2).")
            .That(harness.IndexOf("LookOf(currentLook)", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 06 R1: the look and the visual are built and attached together, and the guard reads
        // the built-from look. The guard pin names the fluent call, so it cannot also match the
        // `if`; the switch pin is the helper call itself, so a switch that keeps the old visual
        // (H2b) or re-attaches the old look's visual (H2c) fails. The guard pin kills G1.
        Check.WithCustomMessage(
                "The harness must build and attach the initial look through AttachLook — "
                + "`var (ring, currentLook) = AttachLook(window, scale, ProbeRingCaptureScene.OwnerLook());` (R1).")
            .That(harness.IndexOf(
                "var (ring, currentLook) = AttachLook(window, scale, ProbeRingCaptureScene.OwnerLook());", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The switch's if-body must be exactly the helper call — `(ring, currentLook) = "
                + "AttachLook(window, scale, step.Look);` — so the visual and the look cannot come apart (H2b/H2c, R1).")
            .That(harness.IndexOf("(ring, currentLook) = AttachLook(window, scale, step.Look);", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The per-capture guard must compare the look the attached visual was built from "
                + "(`That(currentLook.RingStyle).IsEqualTo(step.Look.RingStyle)`); G1 deletes it (R1).")
            .That(harness.IndexOf(
                "That(currentLook.RingStyle).IsEqualTo(step.Look.RingStyle)", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("AttachLook itself must build the visual from the look it returns (R1).")
            .That(harness.IndexOf("internal static (ProbeRingVisual Ring, CursorSettings Look) AttachLook(", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);

        // Fixup 08 item 2: a stale capture must go through the pure retake helper, and the harness
        // must print the retake and stale lines the session record reads.
        Check.WithCustomMessage(
                "CaptureAt must take a stale capture through ProbeRingCaptureRetake.CaptureWithRetake — a "
                + "disabled retake ships the previous frame's pixels into the measurement (fixup 08 item 2).")
            .That(harness.IndexOf("ProbeRingCaptureRetake.CaptureWithRetake(", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must print the R2 retake line for every retake.")
            .That(harness.IndexOf("ProbeRingCaptureRetake.RetakeLine(", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must print the R2 stale line when the copy survives the retakes.")
            .That(harness.IndexOf("ProbeRingCaptureRetake.StaleLine(", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);

        // Fixup 09 F3 (review 07 H1–H4): the four retake-wiring strings. Each mutant of one of these
        // fails closed for judged rows, which is exactly why the source pins must catch it: the
        // retake and the sweep's stale flag are lost silently otherwise.
        Check.WithCustomMessage(
                "The retake must compare against the previous capture's bytes — `previous: previousBgra`; "
                + "a null previous never triggers a retake (H1).")
            .That(harness.IndexOf("previous: previousBgra", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The loop must carry each accepted capture forward — `previousBgra = sample.Bgra;`; "
                + "never updating it disables every retake after the first step (H2).")
            .That(harness.IndexOf("previousBgra = sample.Bgra;", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The sample must record the retake's stale mark — `Stale = retake.Stale`; a hard-coded "
                + "false loses the mark the report and manifest carry (H3).")
            .That(harness.IndexOf("Stale = retake.Stale", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        var waitAt = harness.IndexOf("waitOneMoreFrame: () =>", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "CaptureAt must pass a wait lambda for the retake. Body read:\n" + harness)
            .That(waitAt).IsStrictlyGreaterThan(-1);
        var waitEnd = harness.IndexOf("});", waitAt, StringComparison.Ordinal);
        Check.WithCustomMessage("The wait lambda is unterminated.").That(waitEnd).IsStrictlyGreaterThan(waitAt);
        var waitBody = harness[waitAt..waitEnd];
        Check.WithCustomMessage(
                "The retake's wait must present the frame (window.Present()) before re-capturing; an "
                + "immediate return retakes the same presented frame (H4). Body read:\n" + waitBody)
            .That(waitBody.IndexOf("window.Present()", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The retake's wait must also drain DWM (DwmFlush()) before re-capturing. Body read:\n" + waitBody)
            .That(waitBody.IndexOf("DwmFlush()", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 09 F3 defence in depth: the live samples take the same MarkStale the offline analyser
        // applies, so the two staleness marks cannot diverge.
        Check.WithCustomMessage(
                "The harness must run the live samples through ProbeRingCaptureArchive.MarkStale before "
                + "BuildInputs, as the offline analyser does (fixup 09 F3).")
            .That(harness.IndexOf("MarkStale(samples)", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 10 item 1: the warm-up gate must run before the plan loop, its line must print, and
        // the retake chain must start empty after it (reference B at f = 0 equals the last warm-up
        // frame; run 3's black first frame is what the gate exists for).
        var warmUpAt = harness.IndexOf("ProbeRingWarmUp.Wait(", StringComparison.Ordinal);
        var planLoopAt = harness.IndexOf("foreach (var step in plan)", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The harness must run the warm-up gate (ProbeRingWarmUp.Wait); without it run 3's black "
                + "first frame goes straight into the 21-row scan.")
            .That(warmUpAt).IsStrictlyGreaterThan(-1);
        Check.That(planLoopAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The warm-up must run BEFORE the plan loop.")
            .That(warmUpAt).IsStrictlyLessThan(planLoopAt);
        Check.WithCustomMessage("The harness must print the warm-up line.")
            .That(harness.IndexOf("ProbeRingWarmUp.Line(", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        var resetAt = harness.IndexOf("previousBgra = null;", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The retake chain must start empty AFTER the warm-up: the first plan capture (reference B "
                + "at f = 0) legitimately equals the last warm-up frame and must not be flagged stale.")
            .That(resetAt).IsStrictlyGreaterThan(warmUpAt);

        // Fixup 10 item 2: the intrusion check, its lines, and the live marking.
        Check.WithCustomMessage("CaptureAt must classify intrusions through ProbeRingIntrusion.Check.")
            .That(harness.IndexOf("ProbeRingIntrusion.Check(", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The harness must print the R2 intrusion line for an intruded capture.")
            .That(harness.IndexOf("ProbeRingIntrusion.Line(", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The live samples must take the same ProbeRingIntrusion.Mark the offline analyser applies.")
            .That(harness.IndexOf("ProbeRingIntrusion.Mark(", StringComparison.Ordinal)).IsStrictlyGreaterThan(-1);

        // Fixup 06 R2a: the loop must iterate the plan built here, not a reordered copy (H3b).
        Check.WithCustomMessage(
                "The harness must build the plan as `var plan = ProbeRingCapturePlan.Build();` — a "
                + "reordered Build() would slip past the loop pin (H3b).")
            .That(harness.IndexOf("var plan = ProbeRingCapturePlan.Build();", StringComparison.Ordinal))
            .IsStrictlyGreaterThan(-1);

        // Fixup 06 R2c: the tail order — report, then save, then the single-origin check, then the
        // verdict. Kills O1 (the origin check deleted or moved back before the save).
        var originAt = harness.IndexOf("That(origins).HasSize(1)", StringComparison.Ordinal);
        var judgeAt = harness.IndexOf("ProbeRingOnScreenJudge.Judge(report)", StringComparison.Ordinal);
        Check.WithCustomMessage("The single-origin check must be present (O1 deletes it).")
            .That(originAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The verdict call must be present.")
            .That(judgeAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The tail order must be: close (window gone) → save → measure/report → origin check → "
                + $"verdict — got report {reportAt}, save {saveAt}, origin {originAt}, verdict {judgeAt}.")
            .That(saveAt).IsStrictlyLessThan(reportAt);
        Check.WithCustomMessage("The save must come before the origin check.")
            .That(saveAt).IsStrictlyLessThan(originAt);
        Check.WithCustomMessage("The report must come before the origin check.")
            .That(reportAt).IsStrictlyLessThan(originAt);
        Check.WithCustomMessage("The origin check must come before the verdict.")
            .That(originAt).IsStrictlyLessThan(judgeAt);

        var onScreen = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            root, "tests", "EyeCtrl.App.Tests", "Diagnostics", "SubPixel", "ProbeRingOnScreenTests.cs")));

        const string onScreenMarker = "public void HardwarePath_AtScale2_ReportsFractionalB_AndWholePixelA()";
        var onScreenStart = onScreen.IndexOf(onScreenMarker, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "ProbeRingOnScreenTests no longer declares the on-screen hardware-path test. Repoint this pin.")
            .That(onScreenStart).IsStrictlyGreaterThan(-1);

        var onScreenEnd = onScreen.IndexOf("\n    }", onScreenStart, StringComparison.Ordinal);
        var onScreenBody = onScreen[onScreenStart..onScreenEnd];
        var gate = onScreenBody.IndexOf("AppFixture.SkipIfDisabled()", StringComparison.Ordinal);
        var run = onScreenBody.IndexOf("ProbeRingOnScreenHarness.Run(", StringComparison.Ordinal);

        Check.WithCustomMessage(
                "The on-screen test no longer starts with AppFixture.SkipIfDisabled(), so without "
                + "EYECTRL_UI_TESTS it would report PASS instead of SKIP and quietly mean nothing. "
                + "Body read:\n" + onScreenBody)
            .That(gate).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The on-screen test no longer calls ProbeRingOnScreenHarness.Run(...). Body read:\n" + onScreenBody)
            .That(run).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "AppFixture.SkipIfDisabled() must come before the harness run in the on-screen test. "
                + "Body read:\n" + onScreenBody)
            .That(gate).IsStrictlyLessThan(run);
    }
}
