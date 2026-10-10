namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;

/// <summary>
/// R2 — the on-screen raster test itself (SP-5): the hardware display path at the owner's
/// 3840x2400 @ 200%, proving condition B moves fractionally and condition A lands on whole pixels
/// where R1 only proves it under the software rasterizer.
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
/// The test delegates to <see cref="ProbeRingOnScreenHarness"/>, which holds the window, the
/// DwmFlush-before-capture discipline and every assertion; the headless halves of R2 (the analysis
/// and the gate/source pins) live in <see cref="ProbeRingCaptureAnalysisTests"/> and
/// <see cref="ProbeRingOnScreenGateTests"/> and do run on CI.
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
