namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;

/// <summary>
/// R2 — the on-screen raster test itself (SP-5): the hardware display path at the owner's
/// 3840x2400 @ 200%, proving condition B moves fractionally and condition A lands on whole pixels
/// where R1 only proves it under the software rasterizer.
///
/// <para>
/// <b>Judged on the 21-row average (owner's decision 2026-10-11, after run 2).</b> Every judged
/// value — A, B, the reference A–B check and the sweep — is R1's mirrored-block estimator averaged
/// over the scan rows' centre ± 10 (the run-2 sweep showed the single scan row biased under
/// diagonal motion on the hardware rasterizer); the single row stays as a printed
/// <c>R2 DIAGNOSTIC single-row</c> line, never judged. The harness warms up first (waits for a
/// capture that passes the positive control, ≤ 3 s — run 3's window was black for about a second)
/// and retakes any capture that is a byte-identical copy of the previous frame or carries an
/// overlay over the background (fixup 10); a capture that still fails either is a stale/intruded
/// frame — a judged row fails on it, and the sweep and diagnostics exclude and flag it. The ±0.05
/// tolerance is unchanged.
/// </para>
///
/// <para>
/// <b>Written, not run.</b> This test is <c>Category=UI</c> and gated by
/// <c>EYECTRL_UI_TESTS=1</c>; the CI filter <c>Category!=UI</c> excludes it, and without the
/// variable it reports <b>skipped</b> and creates no window. Running it shows a small topmost
/// window on the owner's display and needs the owner's explicit go — never run it as part of a
/// normal test sweep.
/// </para>
///
/// <para>
/// <b>Owner's run command.</b> The filter is deliberately narrow: a bare <c>Category=UI</c> would
/// also run the app-launching tests, which drive the OS cursor and would take the machine from a
/// gaze-only user.
/// <c>$env:EYECTRL_UI_TESTS='1'; dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ProbeRingOnScreenTests"</c>
/// </para>
///
/// <para>
/// <b>On-screen mutant recipes (review 01 M3 — read literally, two earlier recipes were no-ops).</b>
/// Each mutant must be applied, then this test run with the command above; all three go red for the
/// mutant's own reason once B1/B2 are fixed. With the 21-row judged estimator (owner's decision
/// 2026-10-11, after run 2) m1 and m4 remain killed: snapping moves every scan row together, so
/// the 21-row average still reads the whole-pixel values.
/// <list type="bullet">
/// <item><b>m1 — A never snaps:</b> in <c>ProbeRingVisual.Place</c>, force the <c>snap</c> argument
/// to <c>false</c> when placing condition A. After: A renders like B and the Judge's whole-pixel
/// assertion fails (A at f = 0.25 measures ~0.25 against an expected 0).</item>
/// <item><b>Capture exclusion on:</b> set
/// <see cref="ProbeRingTestWindow.ExcludeFromCapture"/> to <c>true</c> — the one-word edit the
/// switch exists for; it applies <c>SetWindowDisplayAffinity(hwnd, 0x11
/// /* WDA_EXCLUDEFROMCAPTURE */)</c>. The plain window has no <c>ExcludeFromScreenCapture</c>
/// property to flip, which is why the recipe is the Win32 call. After: <c>CopyFromScreen</c> returns
/// the desktop behind the window and the positive control fails.</item>
/// <item><b>m4 — B snaps:</b> <c>src/EyeCtrl.App/Diagnostics/SubPixel/ProbeRingVisual.cs:111</c>,
/// the visual's own <c>SnapsToDevicePixels = false</c> → <c>true</c>. Not the harness container:
/// the visual overrides anything inherited, so a container mutation is an equivalent no-op. After:
/// B measures 0 / 1 / 1 for 0.25 / 0.5 / 0.75 (R1's measured numbers) and the fraction assertion
/// fails. The same mutation also covers the inherited-flag case (R1 F1): the harness window and
/// scene are hostile by construction, so deleting the probe's local <c>false</c> snaps B the same
/// way.</item>
/// </list>
/// </para>
///
/// <para>
/// The test delegates to <see cref="ProbeRingOnScreenHarness"/>, which holds the window and the
/// DwmFlush-before-capture discipline; the judgement lives in <see cref="ProbeRingOnScreenJudge"/>
/// and the geometry in <see cref="ProbeRingFrameSchedule"/>, both driven headlessly by
/// <see cref="ProbeRingOnScreenJudgeTests"/> and <see cref="ProbeRingFrameScheduleTests"/>. The
/// analysis and gate/source pins live in <see cref="ProbeRingCaptureAnalysisTests"/> and
/// <see cref="ProbeRingOnScreenGateTests"/>; all of those run on CI.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class ProbeRingOnScreenTests
{
    private readonly ITestOutputHelper _output;

    public ProbeRingOnScreenTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The whole hardware run: effective DPI 192, positive control, R1's centroid assertions at
    /// f = 0.25 / 0.5 / 0.75, and the fine-fraction sweep recorded to the test output.
    /// </summary>
    [StaFact]
    public void HardwarePath_AtScale2_ReportsFractionalB_AndWholePixelA()
    {
        // The gate before anything else: with the variable unset this test reports SKIPPED, never a
        // pass, and no window is created. The harness checks the same gate before touching WPF — the
        // headless proof of that is ProbeRingOnScreenGateTests.
        AppFixture.SkipIfDisabled();

        // DpiHelper.Initialize is written here, from this test — not from the static harness — so
        // the DpiHelper-writer guard (DpiHelperWriterIsolationTests) sees the write in a file that
        // declares a non-parallel collection. ScreenCaptureService derives its clamp bounds from
        // DpiHelper, and production initializes it at startup; without this the capture would
        // clamp against a half-size screen and measure the wrong place. DpiHelperRestore resets it
        // after the test.
        var outcome = ProbeRingOnScreenHarness.Run(
            _output.WriteLine,
            initializeDisplayScale: scale => DpiHelper.Initialize(scale, scale));
        Check.That(outcome).IsEqualTo(ProbeRingOnScreenOutcome.Completed);
    }
}
