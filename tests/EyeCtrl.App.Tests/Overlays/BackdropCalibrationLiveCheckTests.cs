namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The opacity calibration sweep (the live look's follow-up): the accent API's gradient alpha does
/// not dim linearly — the user's look measured about 8 % dim at alpha 0.3 — so this measures the
/// mapping the slider actually sits on. Six cases, ~6 s each, never activated, all through
/// production's own <c>ApplyBackdrop</c> at the case's alpha:
/// <list type="number">
/// <item>the acrylic blur (state 4, type Mica — Acrylic is the same path) at alpha 0.30, 0.50, 0.70, 0.90;</item>
/// <item>the transparent gradient (state 2, type Solid darken) at alpha 0.30, 0.60.</item>
/// </list>
///
/// <para>
/// Every case runs the rounds-1/2 capture protocol (baseline with the affinity ON, lift,
/// <c>DwmFlush</c> ×2 + 150 ms, capture) and writes invariant machine lines the script reads:
/// <c>state:</c>, <c>alpha:</c>, <c>rectangles:</c>, <c>accent:</c>, <c>ratio-r/g/b:</c>,
/// <c>ratio-luma:</c>, <c>dim:</c>, plus the human readout and the classification.
/// <c>backdrop-calibrate.ps1</c> prints the measured dim against alpha per state and the alpha that
/// would give a 0.70 ratio (a 30 % dim), with an interpolation note.
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

    /// <summary>Case 1 — the blur at 0.30, where the live look measured ~8 %.</summary>
    [StaFact]
    public void TheBlur_AtAlpha030_LogsItsDim() =>
        RunCase(1, "blur-a030", "blur, alpha 0.30", BackdropType.Mica, 0.30);

    /// <summary>Case 2 — the blur at 0.50.</summary>
    [StaFact]
    public void TheBlur_AtAlpha050_LogsItsDim() =>
        RunCase(2, "blur-a050", "blur, alpha 0.50", BackdropType.Mica, 0.50);

    /// <summary>Case 3 — the blur at 0.70.</summary>
    [StaFact]
    public void TheBlur_AtAlpha070_LogsItsDim() =>
        RunCase(3, "blur-a070", "blur, alpha 0.70", BackdropType.Mica, 0.70);

    /// <summary>Case 4 — the blur at 0.90.</summary>
    [StaFact]
    public void TheBlur_AtAlpha090_LogsItsDim() =>
        RunCase(4, "blur-a090", "blur, alpha 0.90", BackdropType.Mica, 0.90);

    /// <summary>Case 5 — the transparent gradient at 0.30.</summary>
    [StaFact]
    public void TheTransparentGradient_AtAlpha030_LogsItsDim() =>
        RunCase(5, "gradient-a030", "transparent gradient, alpha 0.30", BackdropType.SolidDarken, 0.30);

    /// <summary>Case 6 — the transparent gradient at 0.60.</summary>
    [StaFact]
    public void TheTransparentGradient_AtAlpha060_LogsItsDim() =>
        RunCase(6, "gradient-a060", "transparent gradient, alpha 0.60", BackdropType.SolidDarken, 0.60);

    private static void RunCase(int caseNumber, string tag, string label, BackdropType type, double alpha)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Type = type;      // in-memory only: the owner's file is never written
            settings.Opacity = alpha;  // the case's sweep point
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow();
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            window.ApplyBackdrop(settings); // production's own path at this alpha
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop calibration, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var rectangles = SolidRectangles(window);

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

            var accent = type == BackdropType.SolidDarken
                ? window.TryApplyTransparentGradient(alpha)
                : window.TryApplyAcrylic(alpha);
            var state = type == BackdropType.SolidDarken ? 2 : 4;

            var ratioR = lifted.Centre.MeanR / baseline.Centre.MeanR;
            var ratioG = lifted.Centre.MeanG / baseline.Centre.MeanG;
            var ratioB = lifted.Centre.MeanB / baseline.Centre.MeanB;
            var ratioLuma = lifted.Centre.LumaMean / baseline.Centre.LumaMean;

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"state: {state}");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(alpha)}");
            log.AppendLine($"rectangles: {rectangles}");
            log.AppendLine($"accent: {(accent ? "True (accepted)" : "False (misread BOOL or refused)")}");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (expect #00000000)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
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

    /// <summary>The darken fallback's rectangle count on the backdrop canvas.</summary>
    private static int SolidRectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
