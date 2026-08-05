namespace EyeCtrl.App.Tests.Services;

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

    // ── Pause indicator ──

    [Fact]
    public void HideWhilePaused_ShowsIndicator_HideWhileNotPaused_DoesNot()
    {
        var sut = NewController();
        var visPaused = new FakeVisibility();
        sut.Attach(visPaused);
        sut.SetPaused(true);
        sut.Feed(CreateSample(960, 500, valid: true), 0);
        sut.Feed(CreateSample(960, 500, valid: true), 5_001_000);
        Check.That(visPaused.LastHidePaused).IsTrue();  // indicator shown

        var sut2 = NewController();
        var visNotPaused = new FakeVisibility();
        sut2.Attach(visNotPaused);
        sut2.SetPaused(false);
        sut2.Feed(CreateSample(960, 500, valid: true), 0);
        sut2.Feed(CreateSample(960, 500, valid: true), 5_001_000);
        Check.That(visNotPaused.LastHidePaused).IsFalse(); // no indicator
    }

    [Fact]
    public void Pause_WhileHidden_ShowsIndicator()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);            // hidden while NOT paused → no indicator
        Check.That(vis.LastHidePaused).IsFalse();

        sut.SetPaused(true);             // pause AFTER hiding — must show the edge bar
        Check.That(vis.HideCalls).IsEqualTo(2);   // HideForTest + the re-assert
        Check.That(vis.LastHidePaused).IsTrue();
    }

    [Fact]
    public void Resume_WhileHidden_HidesIndicator()
    {
        var sut = NewController();
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);
        sut.HideForTest(vis);            // hidden while paused → indicator up
        Check.That(vis.LastHidePaused).IsTrue();

        sut.SetPaused(false);            // resume while still hidden — must hide the edge bar
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
    public void SummonWhilePaused_HidesIndicator()
    {
        var sut = NewController(); // Top
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.SetPaused(true);
        sut.HideForTest(vis);
        Check.That(vis.LastHidePaused).IsTrue();

        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 401_000);
        Check.That(vis.ShowCalls).IsEqualTo(1);         // toolbar shown, indicator hidden
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
        public void SetActive(bool active) => SetActiveCalls.Add(active);
    }
}
