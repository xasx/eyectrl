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
/// The flicker fix's confirmation look: PRODUCTION's show path only (no paired two-step), with a
/// first-frame readout. Per case the window is shown, held, hidden, and shown again; the display
/// affinity is lifted BEFORE the first show (in the test only) so the screen can be captured the
/// instant <c>ShowBackdrop</c> returns plus two <c>DwmFlush</c> calls (one can return for a
/// composition that began before ShowWindow) — that grab must already read as the
/// finished backdrop (luma within ±10 % of the steady capture; the stddev not the bare desktop's for
/// the blur, and matching the steady state for both) — then again after a ~2.5 s hold, and the same
/// for the re-show:
/// <list type="number">
/// <item>the owner's type at 0.30;</item>
/// <item>Solid darken at 0.30.</item>
/// </list>
///
/// <para>
/// Machine lines: <c>kind</c>, <c>alpha</c>, <c>baseline-luma</c>/<c>baseline-stddev</c>, and for
/// each show (<c>first-</c> and <c>reshow-first-</c>) the first and steady luma/stddev.
/// <c>backdrop-show-look.ps1</c> checks each first frame against its own steady capture and the
/// baseline, and names any offender. The affinity is restored in a <c>finally</c>.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run through
/// <c>C:\t\livecheck-scripts\backdrop-show-look.ps1</c> with the pinned copy <c>C:\t\lc-bd3</c>,
/// only with the owner's go-ahead. No input is simulated and no gaze is fed; the window hides and
/// closes in the <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropShowLookTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(2500);

    /// <summary>Case 1 — the owner's type at 0.30: show, hold, hide, show again, hold.</summary>
    [StaFact]
    public void TheBackdrop_At030_ShowsTheFinishedFrameImmediately_BothTimes() =>
        RunCase(1, "owner-030", "the owner's type at 0.30", solidDarken: false);

    /// <summary>Case 2 — Solid darken at 0.30, the same show/hide/show shape.</summary>
    [StaFact]
    public void TheBackdrop_WithSolidDarkenAt030_ShowsTheFinishedFrameImmediately_BothTimes() =>
        RunCase(2, "solid-030", "Solid darken at 0.30", solidDarken: true);

    private static void RunCase(int caseNumber, string tag, string label, bool solidDarken)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Opacity = 0.30;
            if (solidDarken) settings.Type = BackdropType.SolidDarken; // in-memory only
            var centre = ProductionCentre();
            var kind = solidDarken ? "solid" : "blur";

            window = new GlassBackdropWindow();
            var hwnd = new WindowInteropHelper(window).EnsureHandle(); // the test needs the hwnd to lift the affinity
            var baseline = BackdropCapture.Grab();                     // window still hidden: the desktop

            SetWindowDisplayAffinity(hwnd, WdaNone); // lift BEFORE showing; restored in the finally
            try
            {
                window.ShowBackdrop(settings, centre); // production's own path, first show
                DwmFlush(); // twice: one can return for a composition that began before ShowWindow
                DwmFlush();
                var first = BackdropCapture.Grab();
                LiveCheck.AssertShowsText(window, $"Backdrop show look, case {caseNumber} (first show)", "Mit Esc beenden");
                LiveCheck.AssertVisible(window, $"Backdrop show look, case {caseNumber} (first show)");
                LiveCheck.PumpFor(Hold);
                var steady = BackdropCapture.Grab();

                window.Hide();
                window.ShowBackdrop(settings, centre); // the re-show
                DwmFlush(); // twice: as above
                DwmFlush();
                var reshowFirst = BackdropCapture.Grab();
                LiveCheck.AssertShowsText(window, $"Backdrop show look, case {caseNumber} (re-show)", "Mit Esc beenden");
                LiveCheck.AssertVisible(window, $"Backdrop show look, case {caseNumber} (re-show)");
                LiveCheck.PumpFor(Hold);
                var reshowSteady = BackdropCapture.Grab();

                var log = new StringBuilder();
                log.AppendLine($"case: {caseNumber} ({label})");
                log.AppendLine($"kind: {kind}");
                log.AppendLine($"alpha: {BackdropReadoutLines.Number(settings.Opacity)}");
                log.AppendLine($"Material: {settings.Type} through production's ShowBackdrop");
                log.AppendLine($"Baseline (window hidden; the desktop): {BackdropCapture.Describe(baseline.Centre)}");
                log.AppendLine($"First show, first frame: {BackdropCapture.Describe(first.Centre)}");
                log.AppendLine($"First show, steady: {BackdropCapture.Describe(steady.Centre)}");
                log.AppendLine($"Re-show, first frame: {BackdropCapture.Describe(reshowFirst.Centre)}");
                log.AppendLine($"Re-show, steady: {BackdropCapture.Describe(reshowSteady.Centre)}");
                log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
                log.AppendLine($"baseline-stddev: {BackdropReadoutLines.Number(baseline.Centre.LumaStdDev)}");
                log.AppendLine($"first-luma: {BackdropReadoutLines.Number(first.Centre.LumaMean)}");
                log.AppendLine($"first-stddev: {BackdropReadoutLines.Number(first.Centre.LumaStdDev)}");
                log.AppendLine($"steady-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean)}");
                log.AppendLine($"steady-stddev: {BackdropReadoutLines.Number(steady.Centre.LumaStdDev)}");
                log.AppendLine($"reshow-first-luma: {BackdropReadoutLines.Number(reshowFirst.Centre.LumaMean)}");
                log.AppendLine($"reshow-first-stddev: {BackdropReadoutLines.Number(reshowFirst.Centre.LumaStdDev)}");
                log.AppendLine($"reshow-steady-luma: {BackdropReadoutLines.Number(reshowSteady.Centre.LumaMean)}");
                log.AppendLine($"reshow-steady-stddev: {BackdropReadoutLines.Number(reshowSteady.Centre.LumaStdDev)}");

                BackdropCapture.WriteLog(caseNumber, tag, log.ToString());
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }
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
