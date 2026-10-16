namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
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
/// Sweep 3b: sweep 3 (flags 2 on states 3 and 4) measured a flat, neutral near-black surface — flags 2
/// suppresses the material — so these cases go back to production's OWN call. Three ~6 s cases on a
/// fresh window (CT transparent from <c>SourceInitialized</c>, no <c>ApplyBackdrop</c>), with the
/// <c>SetAccentPolicy</c> setter wrapped so the readout records the state, flags and gradient
/// production's <c>TryApplyAcrylic</c> actually passes:
/// <list type="number">
/// <item><b>REF</b>: <c>window.TryApplyAcrylic()</c> — state 4, flags 0x20, gradient
/// <c>0x01000000</c> (r5's approved glass), no rectangle;</item>
/// <item><b>A1</b>: the same plus a client rectangle in PURE BLACK (#000000) at 0.30, sized to the
/// window, added to <c>BackdropCanvas</c> by the test;</item>
/// <item><b>A2</b>: the same at 0.60.</item>
/// </list>
///
/// <para>
/// Machine lines (invariant): <c>kind</c>, <c>state</c>, <c>flags</c>, <c>gradient-alpha</c>,
/// <c>rect-alpha</c>, <c>stddev-ratio</c>, the per-channel ratios, <c>ratio-luma</c>,
/// <c>baseline-luma</c>, plus the capture readout. <c>backdrop-calibrate.ps1</c> gates on REF first:
/// glass only if stddev-ratio ≤ 0.2 AND ratio-luma ∈ [0.8, 1.6] (a blur keeps the desktop's mean;
/// acrylic brightens a dark one by up to about ×1.3 — near-black fails both); then A1/A2 against REF
/// within ±0.05 of (1 − alpha), each blurred. Neither meets it: "neither".
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

    /// <summary>Case 1 — the reference: production's own blur call at r5's gradient, no rectangle.</summary>
    [StaFact]
    public void TheReference_ProductionBlurAtR5sGradient_LogsItsRatios() =>
        RunCase(1, "ref-blur-0004", "reference", rectAlpha: null);

    /// <summary>Case 2 — production's blur plus a pure-black client rectangle at 0.30.</summary>
    [StaFact]
    public void TheBlur_WithABlackRectangleAt030_LogsItsRatios() =>
        RunCase(2, "blur-rect-030", "blur+rect", rectAlpha: 0.30);

    /// <summary>Case 3 — the same at 0.60.</summary>
    [StaFact]
    public void TheBlur_WithABlackRectangleAt060_LogsItsRatios() =>
        RunCase(3, "blur-rect-060", "blur+rect", rectAlpha: 0.60);

    private static void RunCase(int caseNumber, string tag, string kind, double? rectAlpha)
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

            // Record the exact policy that went to user32: read the default setter (the real native
            // call), then forward through the recorder so state, flags, gradient AND the BOOL are logged.
            var calls = new List<(int State, int Flags, int Gradient, int Result)>();
            var real = window.SetAccentPolicy;
            window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
            {
                var result = real(hwnd, state, flags, gradient);
                calls.Add((state, flags, gradient, result));
                return result;
            };

            window.Show(); // fresh window: the CT is transparent from SourceInitialized; no ApplyBackdrop
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            var applied = window.TryApplyAcrylic(); // production's own call: state 4, flags 0x20, the fixed GlassTint (r5)

            if (rectAlpha is { } alpha)
                AddBlackRectangle(window, alpha);

            window.DrawHideButton(hideButtonCenter);   // after the rectangle: the ✕ stays on top
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop sweep 3b, case {caseNumber} ({kind})";
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
            var stddevRatio = baseline.Centre.LumaStdDev < 0.05
                ? "n/a (a flat baseline)"
                : BackdropReadoutLines.Number(lifted.Centre.LumaStdDev / baseline.Centre.LumaStdDev);

            var recorded = calls.Count > 0 ? calls[^1] : (State: -1, Flags: -1, Gradient: 0, Result: 0);
            var gradientAlpha = ((recorded.Gradient >> 24) & 0xFF) / 255.0;

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({kind})");
            log.AppendLine($"kind: {kind}");
            log.AppendLine(
                $"sent: state={recorded.State} flags=0x{recorded.Flags:X} gradient=0x{recorded.Gradient:X8} "
                + $"-> {(recorded.Result != 0)}");
            log.AppendLine($"state: {recorded.State}");
            log.AppendLine($"flags: {recorded.Flags}");
            log.AppendLine($"gradient-alpha: {BackdropReadoutLines.Number(gradientAlpha)}");
            log.AppendLine(
                $"rect-alpha: {(rectAlpha is { } ra ? BackdropReadoutLines.Number(ra) : "n/a")}");
            log.AppendLine($"rectangles: {rectangles}");
            log.AppendLine($"accent: {(applied ? "True (accepted)" : "False (misread BOOL or refused)")}");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (alpha should be 00)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
            log.AppendLine($"stddev-ratio: {stddevRatio}");
            log.AppendLine($"dark8: {BackdropReadoutLines.Number(lifted.Centre.Dark8Fraction)}");
            log.AppendLine($"dark8-baseline: {BackdropReadoutLines.Number(baseline.Centre.Dark8Fraction)}");
            log.AppendLine($"lifted-luma: {BackdropReadoutLines.Number(lifted.Centre.LumaMean)}");
            log.AppendLine($"lifted-stddev: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev)}");
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
    /// <c>BackdropCanvas</c> — the multiplicative dim over the accent's blur.
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
