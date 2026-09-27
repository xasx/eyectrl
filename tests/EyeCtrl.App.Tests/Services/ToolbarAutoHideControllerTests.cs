namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using NFluent;

public class ToolbarAutoHideControllerTests
{
    // Screen 1920x1080 (physical px). Toolbar docked TOP, rect at top-center 50px tall.
    private const double ScreenW = 1920;
    private const double ScreenH = 1080;
    private static readonly PhysicalRect ToolbarRect = new(810, 0, 300, 50); // top edge
    private const string Edge = "Top";

    // ── Enablement ──

    [Fact]
    public void Disabled_WhenProviderIsMouse_NeverActs()
    {
        var sut = NewController(enabled: false);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // Feed 10s of off-toolbar gaze — must not hide.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void UpdateEnabled_ToFalse_ForceShowsAndResetsToVisible()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.UpdateEnabled(false);

        Check.That(vis.ForceShowCalls).IsEqualTo(1);

        // Feed after disable — must not hide.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);
        Check.That(vis.HideCalls).IsEqualTo(1); // only the initial HideForTest
    }

    // ── HIDE transition ──

    [Fact]
    public void Hides_AfterHideDelay_OfGazeOffToolbar()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);                         // on-screen, off-toolbar
        sut.Feed(CreateSample(960, 500, valid: true), 4_999_000);                 // just under 5s
        Check.That(vis.HideCalls).IsEqualTo(0);                                   // not yet
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000);                 // past 5s
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotHide_WhileGazeOnToolbar()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // Gaze resting on the toolbar for 10s — never hides.
        for (long t = 0; t <= 10_000_000; t += 500_000)
            sut.Feed(CreateSample(960, 25, valid: true), t); // (960,25) inside the toolbar rect

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void GazeBackOnToolbar_ResetsHideCountdown()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);        // off-toolbar
        sut.Feed(CreateSample(960, 500, valid: true), 4_000_000);// 4s in
        sut.Feed(CreateSample(960, 25, valid: true), 4_100_000); // back on toolbar — reset
        sut.Feed(CreateSample(960, 500, valid: true), 9_000_000);// 4.9s since reset — not yet
        Check.That(vis.HideCalls).IsEqualTo(0);
        sut.Feed(CreateSample(960, 500, valid: true), 9_200_000);// past 5s since reset
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    // ── SUMMON transition (directional, off-screen valid coords only) ──

    [Fact]
    public void Summons_OnTopEdge_WhenGazePastTop()
    {
        var sut = NewController(); // Edge = Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);      // start from HIDDEN

        // Valid sample past the top edge (Y < -50 margin)
        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 200_000); // half-way: inside the gap cap
        sut.Feed(CreateSample(960, -100, valid: true), 401_000); // past 400ms summon delay

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void DoesNotSummon_OnWrongEdge_BottomDockedLookingTop()
    {
        var sut = NewController(edge: "Bottom"); // docked BOTTOM
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        // Looking past the TOP edge (Y < -50) — wrong direction for a bottom-docked toolbar.
        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 200_000);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void Summons_OnBottomEdge_WhenGazePastBottom()
    {
        var sut = NewController(edge: "Bottom");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, ScreenH + 100, valid: true), 0);
        sut.Feed(CreateSample(960, ScreenH + 100, valid: true), 200_000);
        sut.Feed(CreateSample(960, ScreenH + 100, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void Summons_OnLeftEdge_WhenGazePastLeft()
    {
        var sut = NewController(edge: "Left");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(-100, 540, valid: true), 0);
        sut.Feed(CreateSample(-100, 540, valid: true), 200_000);
        sut.Feed(CreateSample(-100, 540, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void Summons_OnRightEdge_WhenGazePastRight()
    {
        var sut = NewController(edge: "Right");
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(ScreenW + 100, 540, valid: true), 0);
        sut.Feed(CreateSample(ScreenW + 100, 540, valid: true), 200_000);
        sut.Feed(CreateSample(ScreenW + 100, 540, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void SummonRequires_SustainedOffScreen_SingleSampleDoesNotSummon()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, -100, valid: true), 0);       // one off-screen sample
        sut.Feed(CreateSample(960, 500, valid: true), 100_000);  // back on-screen immediately
        sut.Feed(CreateSample(960, 500, valid: true), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void InvalidSample_DoesNotSummon()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(0, 0, valid: false), 0);
        sut.Feed(CreateSample(0, 0, valid: false), 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    // ── Non-zero-epoch regression tests ──
    // Real trackers emit monotonic µs with an arbitrary large epoch (e.g. 1_000_000_000_000),
    // NOT t=0. These guard against the "0 means unseeded" sentinel bug that caused instant-hide
    // at launch: with a large epoch, E - 0 >= 5_000_000 is true on the first off-toolbar sample.
    // The controller now uses long.MinValue as its unseeded sentinel, so the first off-toolbar
    // sample SEEDS the anchor rather than instantly satisfying the delay.

    [Fact]
    public void DoesNotInstantlyHide_OnLargeEpoch_FirstOffToolbarSample()
    {
        const long Epoch = 1_000_000_000_000L; // realistic arbitrary epoch
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // First off-toolbar sample at a large epoch — must NOT hide immediately.
        sut.Feed(CreateSample(960, 500, valid: true), Epoch);
        Check.That(vis.HideCalls).IsEqualTo(0);

        // ...and still not 1ms later.
        sut.Feed(CreateSample(960, 500, valid: true), Epoch + 1_000);
        Check.That(vis.HideCalls).IsEqualTo(0);

        // Only after the full 5s of sustained off-toolbar gaze.
        sut.Feed(CreateSample(960, 500, valid: true), Epoch + 5_001_000);
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void Summons_OnLargeEpoch_WhenGazePastEdge()
    {
        const long Epoch = 2_000_000_000_000L;
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, -100, valid: true), Epoch);
        sut.Feed(CreateSample(960, -100, valid: true), Epoch + 200_000);
        sut.Feed(CreateSample(960, -100, valid: true), Epoch + 401_000);

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    // ── Blink does not reset the hide countdown ──

    [Fact]
    public void Blink_InvalidSample_DoesNotResetHideCountdown()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 4_000_000); // 4s
        sut.Feed(CreateSample(0, 0, valid: false), 4_100_000);    // blink — ignored, no reset
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000); // past 5s total
        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    // ── Paused hides like any other state ──
    // Owner decision 2026-09-26 (after that day's instrumented run), reversing the 2026-09-24 trap
    // guard (which pinned the band visible while paused after a 1 h 43 min stranding). The
    // 2026-09-24 log shows 10 paused summons and 11 paused auto-hides, and no resume followed any
    // of them. Per the decision, pause no longer touches visibility at all, and the Hidden case
    // below summons with no pause gate. The way back while paused: past-edge gaze (the
    // pause-indicator strip marks that edge), then dwell the Play toggle; the tray ForceShow
    // remains the non-gaze escape hatch.

    [Fact]
    public void WhilePaused_OffToolbarGaze_StillHides()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);

        // 10 s of off-toolbar gaze, well past the 5 s hide delay.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);

        Check.That(vis.HideCalls).IsEqualTo(1);
        Check.That(sut.StateName).IsEqualTo("Hidden");
    }

    [Fact]
    public void WhileNotPaused_HidesWithPausedFalse()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000);

        Check.That(vis.HideCalls).IsEqualTo(1);
        Check.That(vis.LastHidePaused).IsFalse();
    }

    [Fact]
    public void SetPaused_WhileHidden_ReAssertsTheHide()
    {
        // A pause that lands while the band is away touches no visibility: the toolbar stays
        // hidden and its dwell stays deactivated. The re-assert exists because no hide/show
        // transition will fire to carry the pause change into the seam — see SetPaused.
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);            // hidden while NOT paused
        var hideCalls = vis.HideCalls;
        Check.That(hideCalls).IsEqualTo(1);

        sut.SetPaused(true);             // pause AFTER hiding → re-assert, no recovery
        Check.That(vis.ForceShowCalls).IsEqualTo(0);
        Check.That(vis.HideCalls).IsEqualTo(hideCalls + 1);
        Check.That(vis.LastHidePaused).IsTrue();
        Check.That(sut.StateName).IsEqualTo("Hidden");
    }

    [Fact]
    public void Resume_WhileHidden_ReAssertsHideUnpaused()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);
        sut.HideForTest(vis);            // hidden while paused — the normal paused auto-hide
        Check.That(vis.LastHidePaused).IsTrue();

        sut.SetPaused(false);            // resume while still hidden — re-assert the hide, unpaused
        Check.That(vis.LastHidePaused).IsFalse();
    }

    /// <summary>
    /// A no-change <c>SetPaused</c> must not re-assert anything, while a genuine change still acts.
    ///
    /// <para>
    /// Load-bearing, not tidiness. <c>ToolbarLifecycleCoordinator.cs:59-68</c> mirrors every
    /// <c>StateChangedMessage</c> into <c>SetPaused</c>, and that message fires on each
    /// Fixation↔FreeLook flip — several times a second during ordinary gaze. Before the guard, a
    /// hidden toolbar therefore re-asserted <c>HideToolbar</c> at roughly 1.4 Hz indefinitely, each
    /// one costing a dispatcher hop, a <c>DwellDetector.ResetAllState</c>, a <c>Hide()</c> on an
    /// already-hidden window and a <c>PauseIndicator</c> toggle, all on the UI thread that renders
    /// the gaze cursor. A 2026-09-07 live run measured 129 redundant re-asserts against 3 real
    /// hides — which is how the defect was found at all, since it is invisible on screen.
    /// </para>
    /// </summary>
    [Fact]
    public void RedundantSetPaused_DoesNotReAssertAnything()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.SetPaused(true);             // genuine change → recorded, stay visible
        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ForceShowCalls).IsEqualTo(0); // pause touches no visibility while visible

        sut.SetPaused(true);             // the storm: no change, must be ignored
        sut.SetPaused(true);
        sut.SetPaused(true);
        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ForceShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void RedundantResume_WhileHidden_DoesNotReAssertHide()
    {
        // A-M1: the no-change guard must cover the RESUME direction too. The sibling test above
        // only exercises the visible path, so deleting the guard while the controller is hidden
        // and already unpaused survived the whole suite.
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);
        Check.That(vis.HideCalls).IsEqualTo(1);

        sut.SetPaused(false);
        sut.SetPaused(false);
        sut.SetPaused(false);

        Check.That(vis.HideCalls).IsEqualTo(1); // every call is a no-change: nothing may re-assert
    }

    [Fact]
    public void Pause_DoesNotDisturbTheHideCountdown()
    {
        // With paused gaze hiding like any other (2026-09-26), a pause mid-countdown must neither
        // reset the anchor nor pause it: the run keeps counting through the pause and the hide
        // fires on schedule. (The old A-M3 anchor reset belonged to the trap guard, where a pause
        // had to guarantee the band a fresh window on resume.)
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // 4 s of off-toolbar gaze — the countdown is running but has not reached the 5 s delay.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 4_000_000);
        Check.That(vis.HideCalls).IsEqualTo(0);

        sut.SetPaused(true);             // pause must not disturb the countdown
        sut.SetPaused(false);            // resume

        // 5 s since the ORIGINAL anchor: the run counted straight through the pause.
        sut.Feed(CreateSample(960, 500, valid: true), 5_000_000);
        Check.That(vis.HideCalls).IsEqualTo(1);
        Check.That(vis.LastHidePaused).IsFalse();
    }

    [Fact]
    public void Pause_WhileVisible_DoesNotTouchVisibility()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);                 // starts VISIBLE

        sut.SetPaused(true);             // pausing while visible must not hide anything
        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void SummonWhilePaused_ShowsToolbar()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);
        sut.HideForTest(vis);
        Check.That(vis.LastHidePaused).IsTrue();

        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 200_000);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000);
        Check.That(vis.ShowCalls).IsEqualTo(1);         // the way back while paused: past-edge summon
        // The pause-indicator strip is NOT this seam's business (spec H1): ShowToolbar only raises
        // a commanded strip (ToolbarVisibilityAdapter), it never shows or hides one. The paused
        // strip itself is pinned from the coordinator side (SetPauseIndicator on StateChangedMessage).
    }

    [Fact]
    public void Diagnostics_AreGatedToFleetingNothing_AndTwoLinesPerRealAttempt()
    {
        // The diagnostic sink must stay transition-shaped: 2cc6745c's first version logged a
        // "summon run started" per threshold flicker (runs gated on the RESET line only), which at
        // this user's margin clearance (raw y=−50/−52/−56 against −50) could approach half the
        // sample rate — a synchronous file write per line on the gaze thread. These lines exist to
        // make one real run decisive; a flood makes the log useless, so the gate itself is pinned.
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        var log = new List<string>();
        sut.AttachDiagnostics(log.Add);
        sut.HideForTest(vis);

        // Phase 1 — threshold flicker: 33 ms steps alternating 1 px past and 1 px short of the
        // summon threshold for 2 s (~30 started-and-reset runs). Every run is shorter than the
        // 100 ms log gate, so nothing at all may be logged. The trailing band sample resets the
        // pending run so phase 2 starts from Unseeded.
        for (var t = 0L; t <= 2_000_000; t += 33_000)
            sut.Feed(CreateSample(960, t / 33_000 % 2 == 0 ? -51 : -49, valid: true), t);
        sut.Feed(CreateSample(960, -49, valid: true), 2_033_000);
        Check.That(log).IsEmpty();

        // Phase 2 — one sustained attempt: past the threshold for 150 ms, then a sample short of
        // it. Exactly two lines: the gated start and the gated reset.
        log.Clear();
        sut.Feed(CreateSample(960, -51, valid: true), 3_000_000);
        sut.Feed(CreateSample(960, -51, valid: true), 3_150_000);   // 150 ms: the start line fires
        sut.Feed(CreateSample(960, -49, valid: true), 3_183_000);   // broken: the reset line fires
        Check.That(log.Count(l => l.Contains("summon run started"))).IsEqualTo(1);
        Check.That(log.Count(l => l.Contains("summon run reset"))).IsEqualTo(1);

        // The reset sample above sat inside the band, so a band hold is pending; clear it (no
        // line — its entry was never logged) so phase 3 measures its own hold from zero.
        sut.Feed(CreateSample(960, 500, valid: true), 3_500_000);

        // Phase 3 — margin band and blinks, edge-triggered: hover short of the threshold, blink
        // twice, leave. The band line fires once (after a 100 ms hold), the blink contributes one
        // INVALID/valid pair, the leave one exit line — four lines total, however many samples
        // each state lasted.
        log.Clear();
        sut.Feed(CreateSample(960, -20, valid: true), 4_000_000);   // enter band (hold starts)
        sut.Feed(CreateSample(960, -20, valid: true), 4_033_000);   // 33 ms — below the hold gate
        sut.Feed(CreateSample(960, -20, valid: true), 4_100_000);   // 100 ms held: the band line fires
        sut.Feed(CreateSample(960, -20, valid: false), 4_133_000);  // blink — band state frozen
        sut.Feed(CreateSample(960, -20, valid: false), 4_166_000);
        sut.Feed(CreateSample(960, -20, valid: true), 4_200_000);   // still the same band hold
        sut.Feed(CreateSample(960, 500, valid: true), 4_233_000);   // leave the band
        Check.That(log.Count).IsEqualTo(4);
        Check.That(log.Count(l => l.Contains("inside the summon margin band"))).IsEqualTo(1);
        Check.That(log.Count(l => l.Contains("went INVALID"))).IsEqualTo(1);
        Check.That(log.Count(l => l.Contains("valid again"))).IsEqualTo(1);
        Check.That(log.Count(l => l.Contains("left the summon margin band"))).IsEqualTo(1);

        // Phase 4 — a completed summon still announces itself: threshold reached despite the start
        // line's gate (three samples here, so the gated start line fires too; only the threshold
        // line is asserted).
        log.Clear();
        sut.Feed(CreateSample(960, -100, valid: true), 5_000_000);
        sut.Feed(CreateSample(960, -100, valid: true), 5_200_000);
        sut.Feed(CreateSample(960, -100, valid: true), 5_401_000);
        Check.That(log.Count(l => l.Contains("summon threshold reached"))).IsEqualTo(1);
        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    // ── Suspension (keyboard / settings / calibration) ──

    [Fact]
    public void Suspended_DoesNotHide_AndForceShowsIfHidden()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis); // currently hidden

        sut.SetSuspended(true); // e.g. settings window opened
        Check.That(vis.ForceShowCalls).IsEqualTo(1); // recovered from hidden

        // Now feed lots of off-toolbar gaze while suspended — no further hides.
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000);
        Check.That(vis.HideCalls).IsEqualTo(1); // only the initial HideForTest
    }

    [Fact]
    public void UnsuspendedAfterIdle_DoesNotInstantlyHide()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.SetSuspended(true);
        sut.SetSuspended(false);             // just unsuspended, toolbar visible
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 1_000_000); // only 1s
        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    // ── Calibration suspension ──

    [Fact]
    public void CalibrationActive_DoesNotHide()
    {
        var sut = NewController(calibrationActive: () => true);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 10_000_000); // 10s, well past HIDE_DELAY

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    // ── Coordinate space ──

    [Fact]
    public void RectTest_UsesMappedCoordinates_SoRawInsideButMappedOutsideIsOffToolbar()
    {
        // Mapper shifts gaze 500 px down. Raw (960, 25) is inside the top toolbar rect;
        // mapped (960, 525) is not. The controller must treat this as OFF toolbar and hide.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y + 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 25, valid: true), 0);
        sut.Feed(CreateSample(960, 25, valid: true), 6_000_000); // past the 5s hide delay

        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void RectTest_UsesMappedCoordinates_SoRawOutsideButMappedInsideIsOnToolbar()
    {
        // Inverse of the above: raw (960, 525) is outside the rect, mapped (960, 25) is inside.
        // The controller must treat this as ON toolbar and never hide.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y - 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 525, valid: true), 0);
        sut.Feed(CreateSample(960, 525, valid: true), 6_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void EdgeTest_IgnoresTheMapper_AndStaysOnTrackerNativeCoordinates()
    {
        // A mapper that would pull off-screen gaze back on-screen must NOT reach IsPastDockedEdge,
        // or the summon gesture is erased. Raw y = -100 is past the top edge; mapped y = +400 is not.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y + 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 250_000);
        sut.Feed(CreateSample(960, -100, valid: true), 500_000); // past the 400ms summon delay

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void NullMapper_BehavesAsIdentity()
    {
        var sut = NewController(toScreen: null);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 25, valid: true), 0);          // inside the rect
        sut.Feed(CreateSample(960, 25, valid: true), 6_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    // ── ReassertVisibility regression: the calibration-failure close hazard ──
    // Reproduces the exact sequence from the bug report: calibration completes → Feed resumes
    // (IsSessionActive flips false) → off-toolbar gaze hides the toolbar (SetActive(false) via the
    // adapter) → the terminal calibration state arrives "late" (Active/Failed/Idle). The old wiring
    // blind-activated SetActive(true) here, leaving Close dwellable with no toolbar. ReassertVisibility
    // must instead observe the controller's own Hidden state and re-assert the hide.

    [Fact]
    public void ReassertVisibility_AfterHideDuringCalibrationGap_DoesNotReActivateDwell()
    {
        // calibrationActive is a live Func the controller polls each Feed — flip it mid-test, exactly
        // as CalibrationService.IsSessionActive does when CompleteSession runs.
        var calibrating = true;
        var sut = NewController(calibrationActive: () => calibrating);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        // Calibration ends: Feed is no longer suppressed. Off-toolbar gaze now hides the toolbar.
        calibrating = false;
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000); // past HIDE_DELAY → hides
        Check.That(vis.HideCalls).IsEqualTo(1);
        Check.That(sut.StateName).IsEqualTo("Hidden");

        // The terminal calibration state arrives. Old code: _toolbarVisibility?.SetActive(true) → BUG.
        // New code: _toolbarAutoHide?.ReassertVisibility() → re-asserts hide, dwell stays deactivated.
        sut.ReassertVisibility();

        Check.That(vis.HideCalls).IsEqualTo(2);   // hide re-asserted
        Check.That(vis.SetActiveCalls).IsEmpty(); // no blind activation
        Check.That(sut.StateName).IsEqualTo("Hidden");
    }

    // ── ReassertVisibility (calibration-recovery path; the safety fix) ──
    // ReassertVisibility is the state-aware replacement for a blind SetActive(true) after a
    // calibration suppressor ends. It must NOT re-activate dwell when the toolbar auto-hid while
    // Feed was suppressed — that is the exact Close-dwellable-with-no-toolbar hazard.

    [Fact]
    public void ReassertVisibility_WhenHidden_ReAssertsHideAndDoesNotActivateDwell()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis); // toolbar is HIDDEN (e.g. auto-hid during a calibration failure gap)

        sut.ReassertVisibility();

        // THE safety-fix assertion: when hidden, re-assert the hide (HideToolbar — which the real
        // adapter routes through SetActive(false)-before-Hide) and do NOT blind-activate. The fake's
        // HideToolbar is a simple recorder (it does not itself call SetActive), so SetActive(true)
        // not firing is observed directly as an empty SetActiveCalls list.
        Check.That(vis.HideCalls).IsEqualTo(2);        // HideForTest + the re-assert
        Check.That(vis.SetActiveCalls).IsEmpty();      // no blind SetActive(true)
    }

    [Fact]
    public void ReassertVisibility_WhenVisible_ActivatesDwell()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis); // starts VISIBLE

        sut.ReassertVisibility();

        // Visible → activate dwell, no hide.
        Check.That(vis.SetActiveCalls.Count).IsEqualTo(1);
        Check.That(vis.SetActiveCalls[0]).IsTrue();
        Check.That(vis.HideCalls).IsEqualTo(0);
        Check.That(vis.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void ReassertVisibility_WhenDisabled_ActivatesDwell()
    {
        // _enabled == false (floating toolbar / mouse provider): the else-branch must activate so
        // the CalibrationState.Active "complete" trap fix is preserved (no permanent dwell kill).
        var sut = NewController(enabled: false);
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis); // state set Hidden directly; _enabled is false so Feed is inert

        sut.ReassertVisibility();

        Check.That(vis.SetActiveCalls.Count).IsEqualTo(1);
        Check.That(vis.SetActiveCalls[0]).IsTrue();
        Check.That(vis.HideCalls).IsEqualTo(1); // only HideForTest — disabled wins over the hide branch
    }

    [Fact]
    public void ReassertVisibility_BeforeAttach_IsNoOp()
    {
        // Defensive: Attach hasn't wired _visibility yet. Must not throw (the caller —
        // EventCoordinator — invokes ReassertVisibility unconditionally on the terminal states).
        var sut = NewController();

        sut.ReassertVisibility(); // no exception
    }

    // ── helpers ──

    private static ToolbarAutoHideController NewController(bool enabled = true, string edge = Edge,
        Func<bool>? calibrationActive = null, Func<PhysicalPoint, PhysicalPoint>? toScreen = null)
        => new(enabled, edge, ScreenW, ScreenH, ToolbarRect,
               hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50, calibrationActive, toScreen);

    private static GazePoint CreateSample(double x, double y, bool valid)
        => new(x, y, 0, valid);

    // ── fake ──
    private sealed class FakeVisibility : IToolbarVisibility
    {
        public int ShowCalls;
        public int HideCalls;
        public int ForceShowCalls;
        public bool LastHidePaused;
        public List<bool> SetActiveCalls { get; } = new();

        public void ShowToolbar() => ShowCalls++;
        public void HideToolbar(bool paused) { HideCalls++; LastHidePaused = paused; }
        public void ForceShow() => ForceShowCalls++;
        public void PositionIndicator(string edge) { }
        public void SetPauseIndicator(bool visible) { }
        public void SetActive(bool active) => SetActiveCalls.Add(active);
    }
}
