namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
/// The D1 discriminator look (review-10): Solid darken's RE-show read a flat opaque (196, 200, 196) —
/// the owner's system accent lightened — while the first show was correct. This look varies ONE
/// policy variable per case (Solid darken at 0.30 only, show → hold ~2 s → hide → re-show → hold
/// ~2 s → hide):
/// <list type="table">
/// <item><term>S-0</term><description>production as is (should reproduce D1 on the re-show);</description></item>
/// <item><term>S-a</term><description>state 2 with flags 2 instead of 0x20, rewritten in the seam wrapper;</description></item>
/// <item><term>S-b</term><description>the hidden re-show apply's enable is swallowed and the accent is applied AFTER the window is visible (the pre-#214 order);</description></item>
/// <item><term>S-c</term><description>production's order, but DisableAccent's state 0 is swallowed between the shows.</description></item>
/// </list>
///
/// <para>
/// Per grab (<c>baseline</c>, <c>show1-first</c>, <c>show1-steady</c>, <c>reshow-first</c>,
/// <c>reshow-steady</c>, <c>after-hide</c>) the invariant machine lines: mean RGB, luma, stddev,
/// <c>IsWindowVisible</c>, <c>DWMWA_CLOAKED</c>, and every policy call since the previous grab
/// (state/flags/gradient/hwnd → BOOL). <c>backdrop-solid-reshow-look.ps1</c> reads each re-show as
/// <b>finished</b> (steady luma ÷ baseline ≈ 0.70 ±0.05 and not flat), <b>accent fallback</b>
/// (stddev &lt; 1 and luma above the baseline) or <b>other</b>.
/// </para>
///
/// <para>
/// <b>Not a gate, no cloak, no production change.</b> <c>Category=UI</c> and skipped unless
/// <c>EYECTRL_LIVE_CHECK=1</c>; with <c>EYECTRL_LIVE_DRYRUN=1</c> every step runs OFF-SCREEN (the
/// positioner proves the bounds cannot intersect a screen before the show; the visibility proof
/// logs "skipped") so the harness is verified without painting. Run through
/// <c>C:\t\livecheck-scripts\backdrop-solid-reshow-look.ps1</c> with the pinned copy
/// <c>C:\t\lc-bd3</c>, only with the owner's go-ahead.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropSolidReShowLookTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);

    /// <summary>S-0 — production as is.</summary>
    [StaFact]
    public void TheSolidReShow_WithProductionAsIs_ShowsItsColour() =>
        RunCase(1, "s0-production", "S-0 production", Variant.Production);

    /// <summary>S-a — state 2 with flags 2.</summary>
    [StaFact]
    public void TheSolidReShow_WithFlags2_ShowsItsColour() =>
        RunCase(2, "sa-flags2", "S-a state 2, flags 2", Variant.Flags2);

    /// <summary>S-b — the accent applied after the window is visible.</summary>
    [StaFact]
    public void TheSolidReShow_WithTheAccentAppliedAfterShow_ShowsItsColour() =>
        RunCase(3, "sb-after-show", "S-b applied after the show", Variant.AfterShow);

    /// <summary>S-c — DisableAccent's state 0 swallowed.</summary>
    [StaFact]
    public void TheSolidReShow_WithTheDisableSuppressed_ShowsItsColour() =>
        RunCase(4, "sc-no-disable", "S-c no disable", Variant.SuppressDisable);

    private enum Variant
    {
        Production,
        Flags2,
        AfterShow,
        SuppressDisable
    }

    private static void RunCase(int caseNumber, string tag, string label, Variant variant)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Type = BackdropType.SolidDarken; // in-memory only
            settings.Opacity = 0.30;
            var centre = ProductionCentre();
            var dryRun = Environment.GetEnvironmentVariable("EYECTRL_LIVE_DRYRUN") is "1" or "true" or "TRUE";

            window = new GlassBackdropWindow();
            if (dryRun)
                window.PositionForShow = w =>
                {
                    OverlayPositioning.CoverPrimaryScreen(w);
                    w.Left = -40000;
                    w.Top = -40000;
                    var bounds = new Rect(w.Left, w.Top, w.Width, w.Height);
                    var screen = new Rect(
                        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                    Check.WithCustomMessage("dry run: the window must be off-screen before it is shown.")
                        .That(screen.IntersectsWith(bounds)).IsFalse();
                };

            var calls = new List<string>();
            var sinceIndex = 0;
            var swallowNextEnable = false;
            var realPolicy = window.SetAccentPolicy;
            window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
            {
                if (variant == Variant.SuppressDisable && state == 0)
                {
                    calls.Add($"[swallowed] state=0 flags=0x{flags:X} gradient=0x{gradient:X8}");
                    return 1;
                }

                if (variant == Variant.Flags2 && state == 2 && flags == 0x20)
                    flags = 2;

                if (swallowNextEnable && state != 0)
                {
                    swallowNextEnable = false;
                    calls.Add($"[swallowed] state={state} flags=0x{flags:X} gradient=0x{gradient:X8}");
                    return 1;
                }

                var result = realPolicy(hwnd, state, flags, gradient);
                calls.Add(
                    $"state={state} flags=0x{flags:X} gradient=0x{gradient:X8} hwnd=0x{hwnd:X} -> {result != 0}");
                return result;
            };

            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(settings.Opacity)}");
            log.AppendLine($"Material: {settings.Type} through production's ShowBackdrop, variant {variant}");
            if (dryRun) log.AppendLine("proof: visibility skipped (off-screen dry run); the text proof ran");

            void Grab(string name)
            {
                var sample = BackdropCapture.Grab();
                _ = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
                log.AppendLine(
                    $"{name}-mean-rgb: {BackdropReadoutLines.Mean(sample.Centre.MeanR, sample.Centre.MeanG, sample.Centre.MeanB)}");
                log.AppendLine($"{name}-luma: {BackdropReadoutLines.Number(sample.Centre.LumaMean)}");
                log.AppendLine($"{name}-stddev: {BackdropReadoutLines.Number(sample.Centre.LumaStdDev)}");
                log.AppendLine($"{name}-visible: {window.IsVisible}");
                log.AppendLine($"{name}-cloaked: {cloaked}");
                log.AppendLine(
                    $"{name}-policy: {(calls.Count > sinceIndex ? string.Join(" ; ", calls.Skip(sinceIndex)) : "(none)")}");
                sinceIndex = calls.Count;
            }

            Grab("baseline");                         // window hidden: the desktop
            SetWindowDisplayAffinity(hwnd, WdaNone);  // lift BEFORE the shows; restored in the finally
            try
            {
                window.ShowEscapeHint();
                window.ShowBackdrop(settings, centre);
                DwmFlush();
                DwmFlush();
                Grab("show1-first");
                LiveCheck.AssertShowsText(window, $"Solid re-show look, case {caseNumber} (first show)", "Mit Esc beenden");
                if (dryRun) SkipVisibilityProof(caseNumber, "first show"); else LiveCheck.AssertVisible(window, $"Solid re-show look, case {caseNumber} (first show)");
                LiveCheck.PumpFor(Hold);
                Grab("show1-steady");

                window.Hide();
                window.ShowEscapeHint();
                if (variant == Variant.AfterShow)
                {
                    // The pre-#214 order: the accent lands after the window is visible. Swallow the
                    // hidden apply's enable, then apply to the visible (off-screen) window — the same
                    // DWM calls, with only the timing changed.
                    swallowNextEnable = true;
                    window.ShowBackdrop(settings, centre);
                    window.ApplyBackdrop(settings);
                    window.DrawHideButton(centre);
                }
                else
                {
                    window.ShowBackdrop(settings, centre);
                }

                DwmFlush();
                DwmFlush();
                Grab("reshow-first");
                LiveCheck.AssertShowsText(window, $"Solid re-show look, case {caseNumber} (re-show)", "Mit Esc beenden");
                if (dryRun) SkipVisibilityProof(caseNumber, "re-show"); else LiveCheck.AssertVisible(window, $"Solid re-show look, case {caseNumber} (re-show)");
                LiveCheck.PumpFor(Hold);
                Grab("reshow-steady");

                window.Hide();
                Grab("after-hide");
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // restore for every later capture
            }

            BackdropCapture.WriteLog(caseNumber, tag, log.ToString());
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

    /// <summary>
    /// The dry run's visibility proof: logged, not asserted — the window is deliberately OFF-SCREEN.
    /// </summary>
    private static void SkipVisibilityProof(int caseNumber, string phase) =>
        Console.WriteLine(
            $"[solid re-show look] case {caseNumber} ({phase}): visibility proof skipped (off-screen dry run)");

    /// <summary>Production's own hide-button centre (OverlayVmBindingCoordinator.ShowBackdropIfEnabled).</summary>
    private static PhysicalPoint ProductionCentre()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));
    }

    private const int DwmwaCloaked = 14;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
