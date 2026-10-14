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
/// The owner's live look at the material fix (2026-10-14): the backdrop through production's own
/// <c>ApplyBackdrop</c>, once with the owner's real setting (Mica, read-only) and once with
/// <b>Solid darken</b> — Acrylic is now the same code path as Mica, and Solid darken settles a
/// pre-existing question: a non-layered client's alpha is ignored, so is the darken fill black at
/// any opacity, or a real tint over the desktop? Two cases, ~10 s each, never activated; the
/// rounds-1/2 capture readout runs under each (baseline with the affinity ON, lift, <c>DwmFlush</c>
/// ×2 + 150 ms, statistics, comparison-based classification).
///
/// <para>
/// <b>Per-case readout</b> (each is logged; none gates): the darken rectangles on the canvas (0 for
/// a live material, 1 for Solid darken), the accent call's result (re-applied after the capture;
/// expect True — False would mean the accent policy is misread, the B1 inversion), the opacity, the
/// <b>lifted ÷ baseline mean ratio</b> per channel (a blur keeps the mean, so a black tint at 0.3
/// leaves about 0.75; a double dim reads about 0.53), and the session state (EnableTransparency,
/// energy saver) that can suppress materials. Expected: the material case reads <b>varying</b> with
/// no rectangle and a ratio near 0.75; Solid darken reads black at any opacity if the pre-existing
/// suspicion holds.
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
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    /// <summary>Case 1 — the owner's real material (Mica today), read read-only from their settings.</summary>
    [StaFact]
    public void TheBackdrop_WithTheOwnersMaterial_ShowsTheLiveBlur() =>
        RunCase(1, "owner-material", "the owner's material (Mica)", solidDarken: false);

    /// <summary>Case 2 — Solid darken, which settles the black-at-any-opacity question.</summary>
    [StaFact]
    public void TheBackdrop_WithSolidDarken_ShowsWhatTheSolidPathDoes() =>
        RunCase(2, "solid-darken", "Solid darken", solidDarken: true);

    private static void RunCase(int caseNumber, string tag, string label, bool solidDarken)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true; // the live look shows the backdrop itself
            if (solidDarken) settings.Type = BackdropType.SolidDarken; // in-memory only: the file is never written
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            // Production's own hide-button layout, restated from ShowBackdropIfEnabled (:107-111).
            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow();
            window.Show();                        // production order: Show, then the material, then the ✕
            var hwnd = new WindowInteropHelper(window).Handle;
            var ct = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor;

            window.ApplyBackdrop(settings);       // production's own material path
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop live look, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var rectangles = SolidRectangles(window);

            var baseline = BackdropCapture.Grab();   // affinity still ON: the desktop behind
            SetWindowDisplayAffinity(hwnd, WdaNone); // lift, in the test only
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
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }

            // The production call's BOOL is not readable back; a re-apply after the capture gives the
            // same answer (and must be True, or the accent policy is being misread).
            var accentResult = solidDarken
                ? "n/a (Solid darken: the accent is disabled by ClearContent)"
                : $"{(window.TryApplyAcrylic(settings.Opacity) ? "True (accepted)" : "False (misread BOOL!)")} (re-applied after the capture)";

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"Material: {settings.Type} at opacity {settings.Opacity:0.##}");
            log.AppendLine(
                $"Darken rectangles on the canvas: {rectangles} (expect 0 for a live material, 1 for Solid darken)");
            log.AppendLine($"TryApplyAcrylic: {accentResult}");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (expect #00000000)");
            log.AppendLine(BackdropCapture.EnvironmentState());

            var style = GetWindowLong(hwnd, GwlStyle);
            log.AppendLine($"Window styles: style 0x{style:X8} (WS_THICKFRAME: {(style & WsThickFrame) != 0})");
            log.AppendLine($"GetForegroundWindow() == backdrop hwnd: {GetForegroundWindow() == hwnd}");
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropCapture.Describe(lifted.Centre)}");
            log.AppendLine($"dim ratios (lifted / baseline, per channel): {DimRatios(baseline.Centre, lifted.Centre)}");
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

    /// <summary>The per-channel lifted ÷ baseline mean, invariant; n/a when a baseline channel is black.</summary>
    private static string DimRatios(BackdropCapture.SampleStats baseline, BackdropCapture.SampleStats lifted) =>
        baseline.MeanR < 1 || baseline.MeanG < 1 || baseline.MeanB < 1
            ? "n/a (a baseline channel is black)"
            : BackdropReadoutLines.Number(lifted.MeanR / baseline.MeanR) + ";"
              + BackdropReadoutLines.Number(lifted.MeanG / baseline.MeanG) + ";"
              + BackdropReadoutLines.Number(lifted.MeanB / baseline.MeanB);

    /// <summary>The darken fallback's rectangle count on the backdrop canvas.</summary>
    private static int SolidRectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

    private const int GwlStyle = -16;
    private const int WsThickFrame = 0x00040000;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
