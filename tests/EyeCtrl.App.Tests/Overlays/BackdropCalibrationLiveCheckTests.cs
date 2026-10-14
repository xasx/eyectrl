namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The second calibration sweep (the reviewer's design, after the first one measured: state 2 is an
/// exact multiplicative dim, and Solid darken is solved by it; state 4 mixes linearly toward a fixed
/// dark grey and brightens a dark desktop). This one tests <b>blur plus a multiplicative dim</b>,
/// test-only: five ~6 s cases, all on a FRESH window with the CT already transparent from
/// <c>SourceInitialized</c>, no <c>ApplyBackdrop</c>, and the accent called DIRECTLY through
/// <c>SetAccentPolicy</c> — never through <c>BackdropSettings.Opacity</c>, which is guarded 0.1–0.9:
/// <list type="number">
/// <item><b>REF</b>: state 4, gradient <c>0x01000000</c> (r5's approved glass), no rectangle;</item>
/// <item><b>A1</b>: state 4 + a client rectangle in PURE BLACK (#000000, not rgb 18) at alpha 0.30,
/// sized to the window, added to <c>BackdropCanvas</c> by the test;</item>
/// <item><b>A2</b>: the same at 0.60;</item>
/// <item><b>B1</b>: state 3 (ACCENT_ENABLE_BLURBEHIND), flags 2, gradient black at alpha 0.30, no
/// rectangle;</item>
/// <item><b>B2</b>: state 3, flags 2, gradient <c>0x01000000</c> (the state-3 reference).</item>
/// </list>
///
/// <para>
/// Machine lines (invariant): <c>kind</c>, <c>state</c>, <c>flags</c>, <c>gradient-alpha</c>,
/// <c>rect-alpha</c>, <c>stddev-ratio</c>, the per-channel ratios, <c>ratio-luma</c>,
/// <c>baseline-luma</c>, plus the standard capture readout. <c>backdrop-calibrate.ps1</c> reads each
/// case against its reference (REF for A1/A2, B2 for B1): blurred = stddev-ratio ≤ 0.2; dim correct
/// = the case's ratio-luma ÷ the reference's within ±0.05 of (1 − alpha). A1 ≈ 0.7×REF and A2 ≈
/// 0.4×REF, blurred, means "ship state 4 + a black rectangle at alpha"; else B1 ≈ 0.7×B2, blurred,
/// means "ship state 3 + gradient"; else "neither: consider a layered dim window".
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run through
/// <c>C:\t\livecheck-scripts\backdrop-calibrate.ps1</c> with the pinned copy <c>C:\t\lc-bd4</c>,
/// only with the owner's go-ahead. No input is simulated and no gaze is fed; the window closes in a
/// <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropCalibrationLiveCheckTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);

    /// <summary>Case 1 — the reference: the state-4 blur at r5's gradient, no rectangle.</summary>
    [StaFact]
    public void TheReference_BlurAtR5sGradient_LogsItsRatios() =>
        RunCase(1, "ref-blur-0004", "reference", state: 4, flags: 2, gradient: 0x01000000, rectAlpha: null);

    /// <summary>Case 2 — the blur plus a pure-black client rectangle at 0.30.</summary>
    [StaFact]
    public void TheBlur_WithABlackRectangleAt030_LogsItsRatios() =>
        RunCase(2, "blur-rect-030", "blur+rect", state: 4, flags: 2, gradient: 0x01000000, rectAlpha: 0.30);

    /// <summary>Case 3 — the same at 0.60.</summary>
    [StaFact]
    public void TheBlur_WithABlackRectangleAt060_LogsItsRatios() =>
        RunCase(3, "blur-rect-060", "blur+rect", state: 4, flags: 2, gradient: 0x01000000, rectAlpha: 0.60);

    /// <summary>Case 4 — the state-3 blur with a black gradient at 0.30, no rectangle.</summary>
    [StaFact]
    public void TheState3Blur_WithABlackGradientAt030_LogsItsRatios() =>
        RunCase(4, "state3-black-030", "state3", state: 3, flags: 2,
            gradient: GlassBackdropWindow.AccentGradientColor(0.30), rectAlpha: null);

    /// <summary>Case 5 — the state-3 reference: the same blur at r5's gradient.</summary>
    [StaFact]
    public void TheState3Blur_AtR5sGradient_LogsItsRatios() =>
        RunCase(5, "state3-ref-0004", "state3", state: 3, flags: 2, gradient: 0x01000000, rectAlpha: null);

    private static void RunCase(
        int caseNumber, string tag, string kind, int state, int flags, int gradient, double? rectAlpha)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow();
            window.Show(); // fresh window: the CT is transparent from SourceInitialized; no ApplyBackdrop
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            // The accent, called DIRECTLY (the default setter is the real native call). Never through
            // BackdropSettings.Opacity: it is guarded 0.1-0.9 and these gradients are not.
            _ = window.SetAccentPolicy(hwnd, state, flags, gradient);

            if (rectAlpha is { } alpha)
                AddBlackRectangle(window, alpha);

            window.DrawHideButton(hideButtonCenter);   // after the rectangle: the ✕ stays on top
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop sweep 3, case {caseNumber} ({kind})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var rectangles = ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

            var baseline = BackdropCapture.Grab();
            SetWindowDisplayAffinity(hwnd, WdaNone);
            BackdropCapture.ScreenSample lifted;
            try
            {
                DwmFlush();
                DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                lifted = BackdropCapture.Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }

            var ratioR = lifted.Centre.MeanR / baseline.Centre.MeanR;
            var ratioG = lifted.Centre.MeanG / baseline.Centre.MeanG;
            var ratioB = lifted.Centre.MeanB / baseline.Centre.MeanB;
            var ratioLuma = lifted.Centre.LumaMean / baseline.Centre.LumaMean;
            var gradientAlpha = ((gradient >> 24) & 0xFF) / 255.0;
            var stddevRatio = baseline.Centre.LumaStdDev < 0.05
                ? "n/a (a flat baseline)"
                : BackdropReadoutLines.Number(lifted.Centre.LumaStdDev / baseline.Centre.LumaStdDev);

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({kind})");
            log.AppendLine($"kind: {kind}");
            log.AppendLine($"state: {state}");
            log.AppendLine($"flags: {flags}");
            log.AppendLine($"gradient-alpha: {BackdropReadoutLines.Number(gradientAlpha)}");
            log.AppendLine(
                $"rect-alpha: {(rectAlpha is { } ra ? BackdropReadoutLines.Number(ra) : "n/a")}");
            log.AppendLine($"rectangles: {rectangles}");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (alpha should be 00)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
            log.AppendLine($"stddev-ratio: {stddevRatio}");
            log.AppendLine(
                $"black-delta: {BackdropReadoutLines.Number(lifted.Centre.BlackFraction - baseline.Centre.BlackFraction)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
            log.AppendLine($"ratio-r: {BackdropReadoutLines.Number(ratioR)}");
            log.AppendLine($"ratio-g: {BackdropReadoutLines.Number(ratioG)}");
            log.AppendLine($"ratio-b: {BackdropReadoutLines.Number(ratioB)}");
            log.AppendLine($"ratio-luma: {BackdropReadoutLines.Number(ratioLuma)}");
            log.AppendLine($"dim: {BackdropReadoutLines.Number(1.0 - ratioLuma)}");
            log.AppendLine(
                $"case-mean-rgb: {BackdropReadoutLines.Mean(lifted.Centre.MeanR, lifted.Centre.MeanG, lifted.Centre.MeanB)}");
            log.AppendLine($"case-stddev: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev)}");

            var verdict = BackdropClassification.Material(baseline, lifted, log);
            log.AppendLine($"Classification: {verdict}");

            BackdropCapture.WriteLog(caseNumber, tag, log.ToString());
            LiveCheck.PumpFor(Hold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

    /// <summary>
    /// A pure-black client rectangle at <paramref name="alpha"/>, sized to the window and laid on
    /// <c>BackdropCanvas</c> — the multiplicative dim the sweep tests over the accent's blur.
    /// </summary>
    private static void AddBlackRectangle(GlassBackdropWindow window, double alpha)
    {
        var rectangle = new Rectangle
        {
            Width = window.ActualWidth,
            Height = window.ActualHeight,
            Fill = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 0, 0, 0))
        };
        Canvas.SetLeft(rectangle, 0);
        Canvas.SetTop(rectangle, 0);
        ((Canvas)window.FindName("BackdropCanvas")!).Children.Add(rectangle);
    }

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
