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

    // ── helpers ──

    private static ToolbarAutoHideController NewController(bool enabled = true, string edge = Edge,
        Func<bool>? calibrationActive = null)
        => new(enabled, edge, ScreenW, ScreenH, ToolbarRect,
               hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50, calibrationActive);

    private static GazePoint CreateSample(double x, double y, bool valid)
        => new(x, y, 0, valid);

    // ── fake ──
    private sealed class FakeVisibility : IToolbarVisibility
    {
        public int ShowCalls;
        public int HideCalls;
        public int ForceShowCalls;
        public bool LastHidePaused;

        public void ShowToolbar() => ShowCalls++;
        public void HideToolbar(bool paused) { HideCalls++; LastHidePaused = paused; }
        public void ForceShow() => ForceShowCalls++;
        public void PositionIndicator(string edge) { }
    }
}
