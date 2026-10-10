namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EyeCtrl.App.Tests.Fixtures;

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
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TheHarnessSource_FlushesBeforeTheCapture_AndTheUiTestIsGatedFirst()
    {
        var root = SourceScanner.RepoRoot().FullName;

        var harness = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            root, "tests", "EyeCtrl.App.Tests", "Diagnostics", "SubPixel", "ProbeRingOnScreenHarness.cs")));

        const string captureMarker = "private static CapturedFrame CaptureAt(";
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
