namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
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
/// The widened darkness range's confirmation look (owner's go-ahead required): three ~6 s cases
/// through production's own <c>ShowBackdrop</c> (the real show path — position, handle, material
/// and dim prepared while hidden, cloak lifted on the first finished frame):
/// <list type="number">
/// <item><b>Glass at 0.0</b> — the widened floor: pure glass, NO layer at all (<c>rects: 0</c>);
/// the at-uncloak and steady captures must read as the glass (contrast as the glass);</item>
/// <item><b>Glass at 0.95</b> — the widened ceiling: the glass-only capture (the dim layer
/// <c>Collapsed</c> IN THE TEST ONLY) paired with the dimmed one, so a changing desktop cannot
/// distort the pair; the pair ratio is about 0.05, judged on the mean AND the contrast;</item>
/// <item><b>Solid darken at 0.95</b> — about 5 % of the desktop (near black, still see-through);</item>
/// <item><b>Solid darken at 0.0</b> — "shows nothing": the plain desktop (ratio ≈ 1.0), and the
/// policy log shows the accepted state 2 / flags 2 / gradient 0x00000000 call, because state 2 has
/// surprised us before (D1's opaque accent fallback) and a filled-in transparent gradient would be
/// a flat screen exactly where the owner chose "nothing".</item>
/// </list>
///
/// <para>
/// <b>The preflight.</b> <c>TheCentre_IsTheReferenceDesktop</c> runs FIRST and shows nothing: it
/// grabs the same centre patch bare, so the script can stop before any case when the centre is not
/// the reference desktop (the glass bands were measured on the dark wallpaper; windows behind the
/// centre make a CORRECT build read "not the glass"). The per-case guard applies the same
/// thresholds to each glass case's own baseline.
/// </para>
///
/// <para>
/// Machine lines: <c>case</c>, <c>kind</c>, <c>alpha</c>, <c>rects</c>, the baseline luma/stddev;
/// glass: <c>glass-luma</c>, <c>glass-stddev</c>, <c>glass-ratio-luma</c>,
/// <c>glass-stddev-ratio</c>; the 0.95 blur case: <c>dim-luma</c>, <c>dim-stddev</c>,
/// <c>pair-ratio-luma</c>, <c>pair-ratio-stddev</c>, <c>dim-vs-desktop</c>; solid:
/// <c>ratio-luma</c>, <c>stddev-ratio</c>, <c>lifted-luma</c>. <c>backdrop-darkness-look.ps1</c>
/// judges these; on the dark desktop at 0.95 the absolute luma is about 1-2, so the script judges
/// the ratio in a band narrow enough that neither "no dim" (1.0) nor "pure black" (0.0) can pass
/// through the noise, plus an absolute margin.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run
/// through <c>C:\t\livecheck-scripts\backdrop-darkness-look.ps1</c> with the pinned copy
/// <c>C:\t\lc-dr</c>, only with the owner's go-ahead. No input is simulated and no gaze is fed;
/// the window hides and closes in the <c>finally</c>.
/// </para>
///
/// <para>
/// <b>The dry run.</b> With <c>EYECTRL_LIVE_DRYRUN=1</c> as well, every step runs — the show, the
/// cloak/uncloak pumps, all captures, the readout — but the window is shown OFF-SCREEN through a
/// <c>PositionForShow</c> that proves the bounds cannot intersect a screen before the show, and the
/// visibility proof logs "skipped (off-screen dry run)" instead of asserting. The text proof still
/// runs. Every live look is dry-run before the owner is asked.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class BackdropDarknessLookTests
{
    private static readonly TimeSpan SteadyHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>Case 1 — Glass at 0.0: pure glass, no layer.</summary>
    [StaFact]
    public void TheBackdrop_WithGlassAt000_ShowsThePureGlass() =>
        RunCase(1, "glass-000", "Glass at 0.0 (the floor: pure glass, no layer)", BackdropType.Glass, 0.0);

    /// <summary>Case 2 — Glass at 0.95: the glass-only then dimmed pair, ratio about 0.05.</summary>
    [StaFact]
    public void TheBackdrop_WithGlassAt095_ShowsTheGlassThenTheAlmostBlackDim() =>
        RunCase(2, "glass-095", "Glass at 0.95 (the ceiling: about 5 % of the glass)", BackdropType.Glass, 0.95);

    /// <summary>Case 3 — Solid darken at 0.95: about 5 % of the desktop.</summary>
    [StaFact]
    public void TheBackdrop_WithSolidDarkenAt095_ShowsTheAlmostBlackTint() =>
        RunCase(3, "solid-095", "Solid darken at 0.95 (about 5 % of the desktop)", BackdropType.SolidDarken, 0.95);

    /// <summary>Case 4 — Solid darken at 0.0: the plain desktop (the accepted "shows nothing").</summary>
    [StaFact]
    public void TheBackdrop_WithSolidDarkenAt000_ShowsTheDesktop() =>
        RunCase(4, "solid-000", "Solid darken at 0.0 (shows nothing: the desktop itself)", BackdropType.SolidDarken, 0.0);

    /// <summary>
    /// The preflight (review-01 F1a): the reference centre with NOTHING shown — no window, no
    /// material — read before any case. The script runs it first and stops when the centre is not
    /// the reference desktop (luma > 60 or stddev > 46: windows, not the wallpaper), because the
    /// glass bands were measured on that desktop and a correct build over bright content would read
    /// as a failure. Off-screen dry runs take this reading like any other; nothing can be shown.
    /// </summary>
    [StaFact]
    public void TheCentre_IsTheReferenceDesktop()
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        try
        {
            var sample = BackdropCapture.Grab();   // nothing is shown: the bare desktop
            var log = new StringBuilder();
            log.AppendLine("case: 0 (preflight: the centre with nothing shown)");
            log.AppendLine("kind: preflight");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Centre (nothing shown): {BackdropCapture.Describe(sample.Centre)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(sample.Centre.LumaMean)}");
            log.AppendLine($"baseline-stddev: {BackdropReadoutLines.Number(sample.Centre.LumaStdDev)}");
            var dryRun = Environment.GetEnvironmentVariable("EYECTRL_LIVE_DRYRUN") is "1" or "true" or "TRUE";
            if (dryRun)
                log.AppendLine("proof: nothing shown (the preflight)");
            BackdropCapture.WriteLog(0, "preflight", log.ToString());
        }
        finally
        {
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

    private static void RunCase(int caseNumber, string tag, string label, BackdropType type, double alpha)
    {
        Step3LiveCheckTests.BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = Step3LiveCheckTests.BackdropSettingsOfTheOwner();
            settings.Enabled = true;
            settings.Type = type;        // in-memory only
            settings.Opacity = alpha;    // in-memory only
            var centre = ProductionCentre();
            var kind = type == BackdropType.SolidDarken ? "solid" : "blur";

            var dryRun = Environment.GetEnvironmentVariable("EYECTRL_LIVE_DRYRUN") is "1" or "true" or "TRUE";

            window = new GlassBackdropWindow();

            var policyCalls = new List<string>();
            var lastCloak = false;
            var realPolicy = window.SetAccentPolicy;
            window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
            {
                var result = realPolicy(hwnd, state, flags, gradient);
                policyCalls.Add($"state={state} flags=0x{flags:X} gradient=0x{gradient:X8} -> {result != 0}");
                return result;
            };
            var realDwm = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                var result = realDwm(hwnd, attribute, value);
                if (attribute == 13) lastCloak = value;
                return result;
            };

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

            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var baseline = BackdropCapture.Grab();     // window still hidden: the desktop
            SetWindowDisplayAffinity(hwnd, WdaNone);   // lift BEFORE showing; restored in the finally
            BackdropCapture.ScreenSample atUncloak;
            BackdropCapture.ScreenSample steady;
            BackdropCapture.ScreenSample? glassOnly = null;
            try
            {
                window.ShowEscapeHint();
                window.ShowBackdrop(settings, centre);   // production's own path
                DwmFlush();
                DwmFlush();
                PumpUntilUncloaked(() => lastCloak);
                DwmFlush();
                DwmFlush();
                atUncloak = BackdropCapture.Grab();
                LiveCheck.AssertShowsText(window, $"Backdrop darkness look, case {caseNumber} (first show)", "Mit Esc beenden");
                if (dryRun) SkipVisibilityProof(caseNumber); else LiveCheck.AssertVisible(window, $"Backdrop darkness look, case {caseNumber}");
                LiveCheck.PumpFor(SteadyHold);
                steady = BackdropCapture.Grab();
                Check.WithCustomMessage(
                        $"case {caseNumber}: the cloak is STILL UP at the steady grab — a stuck cloak would hide the backdrop.")
                    .That(lastCloak).IsFalse();

                if (type == BackdropType.Glass && alpha > 0 && alpha < 1)
                {
                    // The pair for the ceiling case: the dim layer production drew, collapsed IN THE
                    // TEST ONLY so its absence is the glass alone; same window, same instant range.
                    var dim = Rectangles(window).Single();
                    dim.Visibility = Visibility.Collapsed;
                    DwmFlush();
                    DwmFlush();
                    LiveCheck.PumpFor(TimeSpan.FromMilliseconds(200));
                    glassOnly = BackdropCapture.Grab();
                    dim.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            }

            var rects = Rectangles(window).Count;
            var log = new StringBuilder();
            log.AppendLine($"case: {caseNumber} ({label})");
            log.AppendLine($"kind: {kind}");
            log.AppendLine($"alpha: {BackdropReadoutLines.Number(alpha)}");
            log.AppendLine($"Material: {type} through production's ShowBackdrop");
            log.AppendLine($"rects: {rects}");
            log.AppendLine(BackdropCapture.EnvironmentState());
            log.AppendLine($"Baseline (window hidden; the desktop): {BackdropCapture.Describe(baseline.Centre)}");
            log.AppendLine($"At uncloak: {BackdropCapture.Describe(atUncloak.Centre)}");
            log.AppendLine($"Steady: {BackdropCapture.Describe(steady.Centre)}");
            log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
            log.AppendLine($"baseline-stddev: {BackdropReadoutLines.Number(baseline.Centre.LumaStdDev)}");
            log.AppendLine($"atuncloak-luma: {BackdropReadoutLines.Number(atUncloak.Centre.LumaMean)}");
            log.AppendLine($"steady-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean)}");

            if (type == BackdropType.Glass)
            {
                // The glass-only reading: at the floor it IS the steady capture (no layer exists);
                // at 0.95 it is the paired capture with the dim collapsed.
                var glass = glassOnly ?? steady;
                log.AppendLine($"Glass-only capture: {BackdropCapture.Describe(glass.Centre)}");
                log.AppendLine($"glass-luma: {BackdropReadoutLines.Number(glass.Centre.LumaMean)}");
                log.AppendLine($"glass-stddev: {BackdropReadoutLines.Number(glass.Centre.LumaStdDev)}");
                log.AppendLine($"glass-ratio-luma: {BackdropReadoutLines.Number(glass.Centre.LumaMean / baseline.Centre.LumaMean)}");
                log.AppendLine($"glass-stddev-ratio: {BackdropReadoutLines.Number(glass.Centre.LumaStdDev / baseline.Centre.LumaStdDev)}");

                if (glassOnly is not null)
                {
                    log.AppendLine($"dim-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean)}");
                    log.AppendLine($"dim-stddev: {BackdropReadoutLines.Number(steady.Centre.LumaStdDev)}");
                    log.AppendLine($"pair-ratio-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean / glass.Centre.LumaMean)}");
                    log.AppendLine($"pair-ratio-stddev: {BackdropReadoutLines.Number(steady.Centre.LumaStdDev / glass.Centre.LumaStdDev)}");
                    log.AppendLine($"dim-vs-desktop: {BackdropReadoutLines.Number(steady.Centre.LumaMean / baseline.Centre.LumaMean)}");
                }
            }
            else
            {
                log.AppendLine($"Lifted capture: {BackdropCapture.Describe(steady.Centre)}");
                log.AppendLine($"lifted-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean)}");
                log.AppendLine($"ratio-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean / baseline.Centre.LumaMean)}");
                log.AppendLine($"stddev-ratio: {BackdropReadoutLines.Number(steady.Centre.LumaStdDev / baseline.Centre.LumaStdDev)}");
            }

            log.AppendLine($"policy-calls: {string.Join(" ; ", policyCalls)}");
            if (dryRun)
                log.AppendLine("proof: visibility skipped (off-screen dry run); the text proof ran");
            BackdropCapture.WriteLog(caseNumber, tag, log.ToString());
            LiveCheck.PumpFor(TimeSpan.FromSeconds(4));
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            Step3LiveCheckTests.EndShowcaseCase();
        }
    }

    /// <summary>Pumps until the cloak write observed by the DWM seam has landed (or 800 ms).</summary>
    private static void PumpUntilUncloaked(Func<bool> cloaked)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (cloaked() && clock.ElapsedMilliseconds < 800)
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(5));
    }

    private static void SkipVisibilityProof(int caseNumber) =>
        Console.WriteLine($"[backdrop darkness look] case {caseNumber}: visibility proof skipped (off-screen dry run)");

    private static List<Rectangle> Rectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().ToList();

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
