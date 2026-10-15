namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
/// The owner's final live look (drift-proof): three ~6 s cases through production's own
/// <c>ApplyBackdrop</c>, and the blur cases take PAIRED within-case captures so a changing desktop
/// cannot distort the reading:
/// <list type="number">
/// <item>the owner's type at 0.30 — baseline, then the glass-only capture (the dim layer
/// <c>Collapsed</c> IN THE TEST ONLY), then the dimmed capture (the layer <c>Visible</c>);</item>
/// <item>the same at 0.50;</item>
/// <item>Solid darken at 0.30 (its gradient carries the dim; no rectangle exists).</item>
/// </list>
///
/// <para>
/// Machine lines: <c>kind</c>, <c>alpha</c>, <c>baseline-luma</c>/<c>baseline-stddev</c>; blur:
/// <c>glass-luma</c>, <c>glass-stddev</c>, <c>glass-ratio-luma</c>, <c>glass-stddev-ratio</c>,
/// <c>dim-luma</c>, <c>dim-stddev</c>, <c>pair-ratio-luma</c>, <c>pair-ratio-stddev</c>,
/// <c>dim-vs-desktop</c>; solid: <c>ratio-luma</c>, <c>lifted-luma</c>. The script gates each
/// glass-only capture (ratio-luma in [0.8, 1.6], luma ≥ 10, stddev ≥ 2, stddev-ratio ≤ 0.3), then
/// checks the pair against (1 − α) on the mean and the contrast, and Solid darken's ratio against
/// 0.7. Baseline drift warns but never stops; each dimmed case also prints its darkness against the
/// bare desktop, which is what the owner perceives.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run through
/// <c>C:\t\livecheck-scripts\backdrop-live-look.ps1</c> with the pinned copy <c>C:\t\lc-bd3</c>,
/// only with the owner's go-ahead. No input is simulated and no gaze is fed; the window closes in a
/// <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropLiveLookTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(6);

    /// <summary>Case 1 — the owner's type at 0.30, glass-only then dimmed (paired).</summary>
    [StaFact]
    public void TheBackdrop_At030_ShowsTheGlassAndThenTheDim() =>
        RunBlurCase(1, "owner-030", "the owner's type at 0.30", alpha: 0.30);

    /// <summary>Case 2 — the same at 0.50.</summary>
    [StaFact]
    public void TheBackdrop_At050_ShowsTheGlassAndThenTheDim() =>
        RunBlurCase(2, "owner-050", "the owner's type at 0.50", alpha: 0.50);

    /// <summary>Case 3 — Solid darken at 0.30 through ApplyBackdrop.</summary>
    [StaFact]
    public void TheBackdrop_WithSolidDarkenAt030_ShowsTheTint() =>
        RunSolidCase(3, "solid-030", "Solid darken at 0.30", alpha: 0.30);

    private static void RunBlurCase(int caseNumber, string tag, string label, double alpha)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Opacity = alpha; // the owner's type stays theirs (Mica today); in-memory only
            var hideButtonCenter = ProductionCentre();

            window = new GlassBackdropWindow();
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            window.ApplyBackdrop(settings);
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop live look, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            // The dim layer production added; the TEST collapses it for the glass-only capture.
            var dim = ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Single();
            dim.Visibility = Visibility.Collapsed;

            var baseline = BackdropCapture.Grab(); // affinity still ON: the desktop behind
            SetWindowDisplayAffinity(hwnd, WdaNone);
            BackdropCapture.ScreenSample glass;
            BackdropCapture.ScreenSample dimmed;
            try
            {
                DwmFlush();
                DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                glass = BackdropCapture.Grab();

                dim.Visibility = Visibility.Visible;
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(300));
                dimmed = BackdropCapture.Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine("kind: blur");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(alpha)}");
            log.AppendLine($"Material: {settings.Type} through ApplyBackdrop");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (alpha should be 00)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine($"Glass-only capture: {BackdropCapture.Describe(glass.Centre)}");
            log.AppendLine($"Dimmed capture: {BackdropCapture.Describe(dimmed.Centre)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
            log.AppendLine($"baseline-stddev: {BackdropReadoutLines.Number(baseline.Centre.LumaStdDev)}");
            log.AppendLine($"glass-luma: {BackdropReadoutLines.Number(glass.Centre.LumaMean)}");
            log.AppendLine($"glass-stddev: {BackdropReadoutLines.Number(glass.Centre.LumaStdDev)}");
            log.AppendLine(
                $"glass-ratio-luma: {BackdropReadoutLines.Number(glass.Centre.LumaMean / baseline.Centre.LumaMean)}");
            log.AppendLine(
                $"glass-stddev-ratio: {BackdropReadoutLines.Number(glass.Centre.LumaStdDev / baseline.Centre.LumaStdDev)}");
            log.AppendLine($"dim-luma: {BackdropReadoutLines.Number(dimmed.Centre.LumaMean)}");
            log.AppendLine($"dim-stddev: {BackdropReadoutLines.Number(dimmed.Centre.LumaStdDev)}");
            log.AppendLine(
                $"pair-ratio-luma: {BackdropReadoutLines.Number(dimmed.Centre.LumaMean / glass.Centre.LumaMean)}");
            log.AppendLine(
                $"pair-ratio-stddev: {BackdropReadoutLines.Number(dimmed.Centre.LumaStdDev / glass.Centre.LumaStdDev)}");
            log.AppendLine(
                $"dim-vs-desktop: {BackdropReadoutLines.Number(dimmed.Centre.LumaMean / baseline.Centre.LumaMean)}");

            var verdict = BackdropClassification.Material(baseline, dimmed, log);
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

    private static void RunSolidCase(int caseNumber, string tag, string label, double alpha)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Type = BackdropType.SolidDarken; // in-memory only
            settings.Opacity = alpha;
            var hideButtonCenter = ProductionCentre();

            window = new GlassBackdropWindow();
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            window.ApplyBackdrop(settings);
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop live look, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

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

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine("kind: solid");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(alpha)}");
            log.AppendLine($"Material: {settings.Type} through ApplyBackdrop");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (alpha should be 00)");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine($"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
            log.AppendLine($"lifted-luma: {BackdropReadoutLines.Number(lifted.Centre.LumaMean)}");
            log.AppendLine(
                $"ratio-luma: {BackdropReadoutLines.Number(lifted.Centre.LumaMean / baseline.Centre.LumaMean)}");
            log.AppendLine(
                $"stddev-ratio: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev / baseline.Centre.LumaStdDev)}");

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

    /// <summary>Production's own hide-button centre (OverlayVmBindingCoordinator.ShowBackdropIfEnabled).</summary>
    private static PhysicalPoint ProductionCentre()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));
    }

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
