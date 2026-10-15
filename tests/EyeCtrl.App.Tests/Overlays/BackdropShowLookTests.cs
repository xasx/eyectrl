namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
/// affinity is lifted BEFORE the first show (in the test only) so the screen can be captured.
///
/// <para>
/// Three grabs per show, and which one is judged matters (review-11): (1) the raw first grab, the
/// instant <c>ShowBackdrop</c> returns plus two <c>DwmFlush</c> calls — with no dispatcher pass the
/// cloak is necessarily still up, so it reads the DESKTOP; it is logged as "not yet shown" and never
/// judged. (2) The AT-UNCLOAK grab: pump until the cloak write lands, then flush twice with no
/// pumping and grab — production's flush ran before the uncloak, so DWM keeps the cloaked composition
/// until the next vsync; these flushes present the first composition with the window UNCLOAKED, which
/// is the frame the owner sees first. It must read as the finished backdrop (luma within ±10 % of the steady capture; stddev within
/// ±25 %; the stddev not the bare desktop's for the blur). (3) The steady grab after a ~2.5 s hold —
/// which must also show the cloak is DOWN (a stuck cloak would hide the backdrop from the owner).
/// </para>
///
/// <list type="number">
/// <item>the owner's type at 0.30;</item>
/// <item>Solid darken at 0.30;</item>
/// <item>the magnifier → ring hand-off at 0.30 (shown, then Hide and ShowBackdrop in two back-to-back dispatcher posts).</item>
/// </list>
///
/// <para>
/// Machine lines: <c>kind</c>, <c>handoff</c>, <c>alpha</c>, <c>baseline-luma</c>/<c>baseline-stddev</c>,
/// and for each show (<c>first-</c>/<c>atuncloak-</c>/<c>steady-</c> and <c>reshow-…</c>) the luma/stddev,
/// <c>*-cloaked</c> flags and <c>*-state</c> (IsVisible + cloak). <c>backdrop-show-look.ps1</c> judges
/// the at-uncloak grabs, never judges a cloaked first grab, and fails a case whose steady grab is still
/// cloaked. The affinity is restored in a <c>finally</c>.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> <c>Category=UI</c> and skipped unless <c>EYECTRL_LIVE_CHECK=1</c>; run through
/// <c>C:\t\livecheck-scripts\backdrop-show-look.ps1</c> with the pinned copy <c>C:\t\lc-bd3</c>,
/// only with the owner's go-ahead. No input is simulated and no gaze is fed; the window hides and
/// closes in the <c>finally</c>.
/// </para>
///
/// <para>
/// <b>The dry run.</b> With <c>EYECTRL_LIVE_DRYRUN=1</c> as well, every step runs — both shows, the
/// hide, both captures, the readout — but the window is shown OFF-SCREEN through a
/// <c>PositionForShow</c> that proves the bounds cannot intersect a screen before the show, and the
/// visibility proof logs "skipped (off-screen dry run)" instead of asserting. The text proof still
/// runs. This is how a harness bug (e.g. the Esc hint never being shown, which aborted the owner's
/// first look) is caught without painting anything on the owner's screen; every live look is dry-run
/// before the owner is asked.
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

    /// <summary>
    /// Case 3 — the magnifier → ring hand-off: shown, then <c>Hide</c> and <c>ShowBackdrop</c> in two
    /// back-to-back dispatcher posts (production's shape, and the caller's <c>ShowEscapeHint</c>), with
    /// a grab straight after. The hand-off must not show a bare or undimmed frame: its first frame has
    /// to read as the finished backdrop (which the prepared-content reuse makes possible — nothing is
    /// cleared, re-applied or cloaked).
    /// </summary>
    [StaFact]
    public void TheBackdrop_OnTheMagnifierToRingHandOff_ShowsTheFinishedFrameImmediately() =>
        RunCase(3, "handoff-030", "the magnifier → ring hand-off at 0.30", solidDarken: false, handOff: true);

    private static void RunCase(int caseNumber, string tag, string label, bool solidDarken, bool handOff = false)
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

            // The off-screen dry run: EYECTRL_LIVE_DRYRUN=1 keeps every step but shows the window
            // OFF-SCREEN (and the visibility proof only logs), so a harness bug like the missing
            // hint below can be caught without painting anything on the owner's screen.
            var dryRun = Environment.GetEnvironmentVariable("EYECTRL_LIVE_DRYRUN") is "1" or "true" or "TRUE";

            window = new GlassBackdropWindow();

            // Diagnostics for the readout: every policy call and DWM write, with results; the last
            // cloak value is tracked so each grab can say whether the window was cloaked.
            var policyCalls = new List<string>();
            var dwmCalls = new List<string>();
            var lastCloak = false;
            var realPolicy = window.SetAccentPolicy;
            window.SetAccentPolicy = (hwnd, state, flags, gradient) =>
            {
                var result = realPolicy(hwnd, state, flags, gradient);
                policyCalls.Add($"state={state} flags=0x{flags:X} gradient=0x{gradient:X8} hwnd=0x{hwnd:X} -> {result != 0}");
                return result;
            };
            var realDwm = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                var result = realDwm(hwnd, attribute, value);
                dwmCalls.Add($"attr={attribute} value={value} -> 0x{result:X8}");
                if (attribute == 13) lastCloak = value;
                return result;
            };

            if (dryRun)
                window.PositionForShow = w =>
                {
                    OverlayPositioning.CoverPrimaryScreen(w);
                    w.Left = -40000;
                    w.Top = -40000;
                    // Prove it cannot appear BEFORE the window is shown.
                    var bounds = new Rect(w.Left, w.Top, w.Width, w.Height);
                    var screen = new Rect(
                        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                    Check.WithCustomMessage("dry run: the window must be off-screen before it is shown.")
                        .That(screen.IntersectsWith(bounds)).IsFalse();
                };

            var hwnd = new WindowInteropHelper(window).EnsureHandle(); // the test needs the hwnd to lift the affinity
            var baseline = BackdropCapture.Grab();                     // window still hidden: the desktop
            var baselineState = (window.IsVisible, lastCloak);

            SetWindowDisplayAffinity(hwnd, WdaNone); // lift BEFORE showing; restored in the finally
            try
            {
                // Production's show path does not show the Esc hint (the magnifier path calls it
                // separately); ShowEscapeHint BEFORE ShowBackdrop, so it is part of the prepared
                // frame (it is a sibling outside BackdropCanvas, so ClearContent cannot remove it).
                window.ShowEscapeHint();
                policyCalls.Add("[show1]");
                dwmCalls.Add("[show1]");
                window.ShowBackdrop(settings, centre); // production's own path, first show
                DwmFlush(); // twice: one can return for a composition that began before ShowWindow
                DwmFlush();
                var first = BackdropCapture.Grab();
                var firstState = (window.IsVisible, lastCloak);
                // With no dispatcher pass the cloak is still up, so this grab reads the desktop (the
                // window is not composed) — logged as "not yet shown", never judged. The frame that
                // matters is the AT-UNCLOAK one: pump until the cloak write is False, then flush
                // twice with NO pumping — production's flush ran BEFORE the uncloak (into the still
                // cloaked surface), so the screen keeps the cloaked composition until DWM's next
                // vsync; these flushes present the first composition with the window UNCLOAKED,
                // which is the frame the owner sees first.
                PumpUntilUncloaked(() => lastCloak);
                DwmFlush();
                DwmFlush();
                var atUncloak = BackdropCapture.Grab();
                var atUncloakState = (window.IsVisible, lastCloak);
                LiveCheck.AssertShowsText(window, $"Backdrop show look, case {caseNumber} (first show)", "Mit Esc beenden");
                if (dryRun) SkipVisibilityProof(caseNumber, "first show"); else LiveCheck.AssertVisible(window, $"Backdrop show look, case {caseNumber} (first show)");
                LiveCheck.PumpFor(Hold);
                var steady = BackdropCapture.Grab();
                var steadyState = (window.IsVisible, lastCloak);
                Check.WithCustomMessage(
                        $"case {caseNumber} (first show): the cloak is STILL UP at the steady grab — a stuck cloak would hide the backdrop from the owner.")
                    .That(lastCloak).IsFalse();

                BackdropCapture.ScreenSample reshowFirst;
                BackdropCapture.ScreenSample reshowAtUncloak;
                BackdropCapture.ScreenSample reshowSteady;
                (bool Visible, bool Cloak) reshowFirstState;
                (bool Visible, bool Cloak) reshowAtUncloakState;
                (bool Visible, bool Cloak) reshowSteadyState;
                var handoffHides = 0;
                if (handOff)
                {
                    // Production's hand-off: Hide and ShowBackdrop (and the caller's hint) in two
                    // back-to-back dispatcher posts, ~1 ms apart. Drain at NORMAL priority — not
                    // Background, which would first run the Render base.Show() posted and hide the
                    // very race this case exists to catch — so only the hand-off's posts run, then
                    // grab straight after, the worst case.
                    policyCalls.Add("[handoff]");
                    dwmCalls.Add("[handoff]");
                    // The hand-off must never take the window off the screen (the deferred hide is
                    // cancelled by the show), so every transition to invisible in this window is
                    // counted and read out.
                    void OnHandoffVisibleChanged(object? s, DependencyPropertyChangedEventArgs e)
                    {
                        if (!window.IsVisible) handoffHides++;
                    }

                    window.IsVisibleChanged += OnHandoffVisibleChanged;
                    try
                    {
                        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(window.Hide));
                        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => window.ShowBackdrop(settings, centre)));
                        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(window.ShowEscapeHint));
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Normal);
                        DwmFlush(); // twice: as above
                        DwmFlush();
                        reshowFirst = BackdropCapture.Grab();
                        reshowFirstState = (window.IsVisible, lastCloak);
                        LiveCheck.AssertShowsText(window, $"Backdrop show look, case {caseNumber} (hand-off)", "Mit Esc beenden");
                        if (dryRun) SkipVisibilityProof(caseNumber, "hand-off"); else LiveCheck.AssertVisible(window, $"Backdrop show look, case {caseNumber} (hand-off)");
                        PumpUntilUncloaked(() => lastCloak);
                        DwmFlush(); // twice, no pumping: present the first composition with the window UNCLOAKED
                        DwmFlush();
                        reshowAtUncloak = BackdropCapture.Grab();
                        reshowAtUncloakState = (window.IsVisible, lastCloak);
                        LiveCheck.PumpFor(Hold); // drain well past Background: a leaked hide would run here
                        reshowSteady = BackdropCapture.Grab();
                        reshowSteadyState = (window.IsVisible, lastCloak);
                    }
                    finally
                    {
                        window.IsVisibleChanged -= OnHandoffVisibleChanged;
                    }

                    Check.WithCustomMessage(
                            $"case {caseNumber} (hand-off): the cloak is STILL UP at the steady grab — a stuck cloak.")
                        .That(lastCloak).IsFalse();
                }
                else
                {
                    window.Hide();
                    // The hand-off window is ~a frame; hold the window hidden for about a second so
                    // this re-show takes the REAL prepare-and-cloak path (the owner's original
                    // "re-show flashed" reading) instead of the fast hand-off path — the script
                    // checks the path each show took (a wrong path is an OFF).
                    LiveCheck.PumpFor(TimeSpan.FromMilliseconds(1000));
                    window.ShowEscapeHint(); // Hide() collapsed it; the re-show prepares it again
                    policyCalls.Add("[show2]");
                    dwmCalls.Add("[show2]");
                    window.ShowBackdrop(settings, centre); // the re-show
                    DwmFlush(); // twice: as above
                    DwmFlush();
                    reshowFirst = BackdropCapture.Grab();
                    reshowFirstState = (window.IsVisible, lastCloak);
                    LiveCheck.AssertShowsText(window, $"Backdrop show look, case {caseNumber} (re-show)", "Mit Esc beenden");
                    if (dryRun) SkipVisibilityProof(caseNumber, "re-show"); else LiveCheck.AssertVisible(window, $"Backdrop show look, case {caseNumber} (re-show)");
                    PumpUntilUncloaked(() => lastCloak);
                    DwmFlush(); // twice, no pumping: present the first composition with the window UNCLOAKED
                    DwmFlush();
                    reshowAtUncloak = BackdropCapture.Grab();
                    reshowAtUncloakState = (window.IsVisible, lastCloak);
                    LiveCheck.PumpFor(Hold);
                    reshowSteady = BackdropCapture.Grab();
                    reshowSteadyState = (window.IsVisible, lastCloak);
                    Check.WithCustomMessage(
                            $"case {caseNumber} (re-show): the cloak is STILL UP at the steady grab — a stuck cloak.")
                        .That(lastCloak).IsFalse();
                }

                // The case-2 mystery probe: one more grab right after the final Hide. If the reading
                // persists with the window hidden, it is not the backdrop.
                policyCalls.Add("[final-hide]");
                dwmCalls.Add("[final-hide]");
                window.Hide();
                var afterHide = BackdropCapture.Grab();
                var afterHideState = (window.IsVisible, lastCloak);

                static string Fmt((bool Visible, bool Cloak) state) =>
                    $"IsVisible={state.Visible}, cloak={state.Cloak}";

                var log = new StringBuilder();
                log.AppendLine($"case: {caseNumber} ({label})");
                log.AppendLine($"kind: {kind}");
                log.AppendLine($"handoff: {(handOff ? "true" : "false")}");
                // The path each show took, from the observed cloak state at its raw first grab: a
                // prepared show is necessarily still cloaked there (nothing pumped the dispatcher), a
                // hand-off show is never cloaked. The script checks cases 1-2's re-show took the cloak
                // path and case 3's hand-off took the fast path.
                log.AppendLine($"path: {(firstState.Item2 ? "prepared+cloak" : "hand-off")}");
                log.AppendLine($"reshow-path: {(reshowFirstState.Item2 ? "prepared+cloak" : "hand-off")}");
                // The hand-off's deferred hide must never take the window off the screen.
                log.AppendLine($"handoff-hide: {(handOff ? (handoffHides > 0 ? "hid" : "none") : "n/a")}");
                log.AppendLine($"alpha: {BackdropReadoutLines.Number(settings.Opacity)}");
                log.AppendLine($"Material: {settings.Type} through production's ShowBackdrop");
                if (dryRun)
                    log.AppendLine("proof: visibility skipped (off-screen dry run); the text proof ran");
                log.AppendLine($"Baseline (window hidden; the desktop): {BackdropCapture.Describe(baseline.Centre)}");
                log.AppendLine($"First show, first frame: {BackdropCapture.Describe(first.Centre)}");
                log.AppendLine($"First show, at uncloak: {BackdropCapture.Describe(atUncloak.Centre)}");
                log.AppendLine($"First show, steady: {BackdropCapture.Describe(steady.Centre)}");
                log.AppendLine($"Re-show, first frame: {BackdropCapture.Describe(reshowFirst.Centre)}");
                log.AppendLine($"Re-show, at uncloak: {BackdropCapture.Describe(reshowAtUncloak.Centre)}");
                log.AppendLine($"Re-show, steady: {BackdropCapture.Describe(reshowSteady.Centre)}");
                log.AppendLine($"baseline-luma: {BackdropReadoutLines.Number(baseline.Centre.LumaMean)}");
                log.AppendLine($"baseline-stddev: {BackdropReadoutLines.Number(baseline.Centre.LumaStdDev)}");
                log.AppendLine($"first-luma: {BackdropReadoutLines.Number(first.Centre.LumaMean)}");
                log.AppendLine($"first-stddev: {BackdropReadoutLines.Number(first.Centre.LumaStdDev)}");
                log.AppendLine($"first-cloaked: {(firstState.Item2 ? "true" : "false")}");
                log.AppendLine($"atuncloak-luma: {BackdropReadoutLines.Number(atUncloak.Centre.LumaMean)}");
                log.AppendLine($"atuncloak-stddev: {BackdropReadoutLines.Number(atUncloak.Centre.LumaStdDev)}");
                log.AppendLine($"steady-luma: {BackdropReadoutLines.Number(steady.Centre.LumaMean)}");
                log.AppendLine($"steady-stddev: {BackdropReadoutLines.Number(steady.Centre.LumaStdDev)}");
                log.AppendLine($"reshow-first-luma: {BackdropReadoutLines.Number(reshowFirst.Centre.LumaMean)}");
                log.AppendLine($"reshow-first-stddev: {BackdropReadoutLines.Number(reshowFirst.Centre.LumaStdDev)}");
                log.AppendLine($"reshow-first-cloaked: {(reshowFirstState.Item2 ? "true" : "false")}");
                log.AppendLine($"reshow-atuncloak-luma: {BackdropReadoutLines.Number(reshowAtUncloak.Centre.LumaMean)}");
                log.AppendLine($"reshow-atuncloak-stddev: {BackdropReadoutLines.Number(reshowAtUncloak.Centre.LumaStdDev)}");
                log.AppendLine($"reshow-steady-luma: {BackdropReadoutLines.Number(reshowSteady.Centre.LumaMean)}");
                log.AppendLine($"reshow-steady-stddev: {BackdropReadoutLines.Number(reshowSteady.Centre.LumaStdDev)}");
                log.AppendLine($"after-hide-luma: {BackdropReadoutLines.Number(afterHide.Centre.LumaMean)}");
                log.AppendLine($"after-hide-stddev: {BackdropReadoutLines.Number(afterHide.Centre.LumaStdDev)}");
                log.AppendLine($"baseline-state: {Fmt(baselineState)}");
                log.AppendLine($"first-state: {Fmt(firstState)}");
                log.AppendLine($"atuncloak-state: {Fmt(atUncloakState)}");
                log.AppendLine($"steady-state: {Fmt(steadyState)}");
                log.AppendLine($"reshow-first-state: {Fmt(reshowFirstState)}");
                log.AppendLine($"reshow-atuncloak-state: {Fmt(reshowAtUncloakState)}");
                log.AppendLine($"reshow-steady-state: {Fmt(reshowSteadyState)}");
                log.AppendLine($"after-hide-state: {Fmt(afterHideState)}");
                log.AppendLine($"policy-calls: {string.Join(" ; ", policyCalls)}");
                log.AppendLine($"dwm-calls: {string.Join(" ; ", dwmCalls)}");

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

    /// <summary>
    /// Pumps the dispatcher until the cloak write observed by the DWM seam has landed (or an 800 ms
    /// timeout). The caller then calls <c>DwmFlush</c> TWICE with no pumping before the grab:
    /// production's flush ran BEFORE the uncloak (into the still-cloaked surface), so the screen keeps
    /// showing the cloaked composition until DWM's next vsync — the flushes present the first
    /// composition with the window UNCLOAKED, which is the frame the owner sees first.
    /// </summary>
    private static void PumpUntilUncloaked(Func<bool> cloaked)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (cloaked() && clock.ElapsedMilliseconds < 800)
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(5));
    }

    /// <summary>
    /// The dry run's visibility proof: logged, not asserted — the window is deliberately OFF-SCREEN,
    /// so the strict on-screen rule cannot hold; the text proof still ran.
    /// </summary>
    private static void SkipVisibilityProof(int caseNumber, string phase) =>
        Console.WriteLine(
            $"[backdrop show look] case {caseNumber} ({phase}): visibility proof skipped (off-screen dry run)");

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
