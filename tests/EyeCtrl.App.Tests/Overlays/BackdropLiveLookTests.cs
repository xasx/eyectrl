namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
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
/// <c>ApplyBackdrop</c> with the owner's real setting (Mica, read-only) and once more with Acrylic —
/// both now the legacy accent blur. Two cases, ~10 s each, never activated; the rounds-1/2 capture
/// readout runs under each (baseline with the affinity ON, lift, <c>DwmFlush</c> ×2 + 150 ms,
/// statistics, comparison-based classification) and the expectation is <b>varying</b> — a live
/// blur. Black would mean no material reached the surface; flat would mean a fallback; UNREADABLE
/// means the lift missed the capture and the case should be re-run.
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
        RunCase(1, "owner", "the owner's material (Mica)", forceAcrylic: false);

    /// <summary>Case 2 — Acrylic, to show both named settings produce the same live blur.</summary>
    [StaFact]
    public void TheBackdrop_WithAcrylic_ShowsTheLiveBlur() =>
        RunCase(2, "acrylic", "Acrylic", forceAcrylic: true);

    private static void RunCase(int caseNumber, string tag, string label, bool forceAcrylic)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true; // the live look shows the backdrop itself
            if (forceAcrylic) settings.Type = BackdropType.Acrylic; // in-memory only: the file is never written
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

            window.ApplyBackdrop(settings);       // production's own material path — the accent blur
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();

            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            var name = $"Backdrop live look, case {caseNumber} ({label})";
            LiveCheck.AssertShowsText(window, name, "Mit Esc beenden");
            LiveCheck.AssertVisible(window, name);

            var baseline = BackdropAbLiveCheckTests.Grab();   // affinity still ON: the desktop behind
            SetWindowDisplayAffinity(hwnd, WdaNone);          // lift, in the test only
            BackdropAbLiveCheckTests.ScreenSample lifted;
            try
            {
                DwmFlush();
                DwmFlush();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(150));
                lifted = BackdropAbLiveCheckTests.Grab();
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }

            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"Material: {settings.Type} at opacity {settings.Opacity:0.##} (the accent blur)");
            log.AppendLine(
                $"CompositionTarget.BackgroundColor after Show: #{ct.R:X2}{ct.G:X2}{ct.B:X2}{ct.A:X2} (expect #00000000)");

            var style = GetWindowLong(hwnd, GwlStyle);
            log.AppendLine($"Window styles: style 0x{style:X8} (WS_THICKFRAME: {(style & WsThickFrame) != 0})");
            log.AppendLine($"GetForegroundWindow() == backdrop hwnd: {GetForegroundWindow() == hwnd}");
            log.AppendLine($"Baseline (affinity ON; the desktop behind): {BackdropAbLiveCheckTests.Describe(baseline.Centre)}");
            log.AppendLine(
                $"Capture (affinity lifted, DwmFlush x2 + 150 ms): {BackdropAbLiveCheckTests.Describe(lifted.Centre)}");
            log.AppendLine(
                $"case-mean-rgb: {BackdropReadoutLines.Mean(lifted.Centre.MeanR, lifted.Centre.MeanG, lifted.Centre.MeanB)}");
            log.AppendLine($"case-stddev: {BackdropReadoutLines.Number(lifted.Centre.LumaStdDev)}");

            var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, log);
            log.AppendLine($"Classification: {verdict}");

            BackdropAbLiveCheckTests.WriteLog(caseNumber, tag, log.ToString());
            LiveCheck.PumpFor(Hold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

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
