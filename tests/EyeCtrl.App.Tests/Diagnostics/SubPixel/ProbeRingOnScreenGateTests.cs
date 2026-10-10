namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.IO;
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
/// <item>The source pins: <c>DwmFlush()</c> must call <i>before</i> the capture inside the one
/// method that captures, and the on-screen test must call <see cref="AppFixture.SkipIfDisabled"/>
/// before it calls the harness. Both are the kind of ordering that a future edit can invert with
/// everything else still compiling and the headless suite still green.</item>
/// </list>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingOnScreenGateTests
{
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
