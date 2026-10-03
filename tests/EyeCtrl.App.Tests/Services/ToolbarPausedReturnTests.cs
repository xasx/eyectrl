namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The paused return path end to end, headless: the real <see cref="ToolbarAutoHideController"/>,
/// the real <see cref="ToolbarVisibilityAdapter"/> and the real <c>ToolbarViewModel</c> over a fake
/// window, driven by a real <c>InteractionManager</c>, at the user's physical 3840 × 2400 geometry.
///
/// <para>
/// Every step of this chain was pinned somewhere (<c>ToolbarAutoHideControllerTests</c>,
/// <c>ToolbarVisibilityAdapterHeadlessTests</c>, <c>ToolbarVisibilityAdapterTests</c>,
/// <c>ToolbarPausedTargetAlignmentTests</c>), but nothing composed them — <c>SummonWhilePaused_ShowsToolbar</c>
/// starts from <c>HideForTest</c>, not from a paused hide. Since the 2026-09-26 decision this chain
/// is the only gaze way out of Paused, so it gets one test that walks it: paused → off-toolbar gaze
/// → Hidden (dwell deactivated) → past-edge gaze → Visible (dwell re-armed) → Play dwelled on paused
/// samples → the interaction leaves Paused.
/// </para>
///
/// <para>
/// The other two pin the summon run's tolerance for gaze loss (spec §State machine: "Invalid
/// samples reset neither timer"): a blink inside the 400 ms run keeps it counting, but a gap longer
/// than the 300 ms cap ends the run, so returning from a walk-away does not summon.
/// </para>
/// </summary>
public class ToolbarPausedReturnTests
{
    // The user's physical display: the thresholds the whole review turns on (raw y = −50/−52/−56
    // against a −50 px margin) only mean anything at this scale.
    private const double W = 3840, H = 2400;

    private sealed class FakeToolbar : IToolbarOverlay
    {
        public bool IsVisible { get; private set; } = true;
        public bool TargetsInitialized => true;
        public bool IsSliding => false;
        public bool IsOnScreen => IsVisible;
        public double ExtentDip => 136;
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void ShowAtRest() => IsVisible = true;
        public void ShowThenSlideIn() => IsVisible = true;
        public void SlideOutThenHide(Action onHidden) { IsVisible = false; onHidden(); }
        public void Initialize(double buttonSize, int dwellTimeMs) { }
        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public PhysicalRect GetPhysicalBounds() => new(0, 0, 0, 0);
    }

    private sealed class FakePauseIndicator : IPauseIndicatorOverlay
    {
        public void Show() { }
        public void Hide() { }
        public void SetEdge(string edge) { }
        public void RaiseToTop() { }
    }

    /// <summary>Runs the adapter's queued dispatcher work. No STA thread required — nothing here is a Window.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Per edge: toolbar rect (physical), the Pause/Play face centre, and a raw past-edge sample.
    private static (PhysicalRect Rect, PhysicalPoint Play, PhysicalPoint PastEdge) Geo(string edge) => edge switch
    {
        "Top"    => (new PhysicalRect(920, 0, 2000, 272), new PhysicalPoint(1100, 136), new PhysicalPoint(1920, -100)),
        "Bottom" => (new PhysicalRect(920, H - 272, 2000, 272), new PhysicalPoint(1100, H - 136), new PhysicalPoint(1920, H + 100)),
        "Left"   => (new PhysicalRect(0, 200, 272, 2000), new PhysicalPoint(136, 380), new PhysicalPoint(-100, 1200)),
        "Right"  => (new PhysicalRect(W - 272, 200, 272, 2000), new PhysicalPoint(W - 136, 380), new PhysicalPoint(W + 100, 1200)),
        _ => throw new ArgumentException(edge),
    };

    [Theory]
    [InlineData("Top", false)]
    [InlineData("Bottom", false)]
    [InlineData("Left", false)]
    [InlineData("Right", false)]
    [InlineData("Top", true)]     // pause lands while hidden (tray pause after an unpaused hide)
    [InlineData("Right", true)]
    public void PausedUser_GetsBack_ByGazeAlone(string edge, bool pauseWhileHidden)
    {
        var (rect, play, past) = Geo(edge);
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var interaction = new InteractionManager();
        // The production chain, in miniature: the toolbar's Play toggle sends PauseToggledMessage,
        // and the coordinator mirrors the resulting state into the controller (ToolbarLifecycleCoordinator
        // does both — the mirror `sut.SetPaused` call is the part under test here).
        messenger.Register<PauseToggledMessage>(this, (_, _) => interaction.TogglePause());

        var vm = new ExposedDwellToolbarViewModel(settings, messenger);
        vm.UpdateButtonScreenPosition("Pause", play);
        var toolbar = new FakeToolbar();
        var overlays = TestFakes.MakeOverlayManager(toolbar: toolbar, pauseIndicator: new FakePauseIndicator());
        var adapter = new ToolbarVisibilityAdapter(overlays, Dispatcher.CurrentDispatcher, vm);
        var sut = new ToolbarAutoHideController(true, edge, W, H, rect, 5000, 400, 50);
        sut.Attach(adapter);
        var diag = new List<string>();
        sut.AttachDiagnostics(diag.Add);

        long t = 1_000_000_000_000L;
        void Sample(PhysicalPoint p, bool valid = true)
        {
            var paused = interaction.CurrentState == InteractionState.Paused;
            sut.Feed(new GazePoint(p.X, p.Y, t, valid), t);
            if (valid)
                vm.Receive(new GazeCursorMovedMessage(p, t) { IsPaused = paused });
            t += 33_000;
        }
        void TogglePauseViaMirror()
        {
            interaction.TogglePause();
            sut.SetPaused(interaction.CurrentState == InteractionState.Paused);
        }

        if (!pauseWhileHidden) TogglePauseViaMirror();       // paused while the band is up

        // 5.1 s of ordinary off-toolbar gaze (screen centre). Paused hides like any other state.
        var centre = new PhysicalPoint(1920, 1200);
        for (var i = 0; i < 155; i++) Sample(centre);
        Pump();
        Check.That(sut.StateName).IsEqualTo("Hidden");
        Check.That(toolbar.IsVisible).IsFalse();

        if (pauseWhileHidden)
        {
            TogglePauseViaMirror();                          // tray pause while hidden
            Pump();
            Check.That(sut.StateName).IsEqualTo("Hidden");
        }
        Check.That(interaction.CurrentState).IsEqualTo(InteractionState.Paused);

        // Summon: 450 ms of past-edge gaze. The pause-indicator strip marks this edge for the user.
        for (var i = 0; i < 14; i++) Sample(past);
        Pump();
        Check.That(sut.StateName).IsEqualTo("Visible");
        Check.That(toolbar.IsVisible).IsTrue();

        // Dwell Play on paused samples: the toolbar band stays dwellable while paused, which is the
        // whole way back (DwellOverlayViewModelBase.DwellActiveWhilePaused opts out of the gate).
        var steps = (settings.Dwell.ToolbarButtonDwellMs + 150) / 33;
        for (var i = 0; i < steps; i++) Sample(play);
        Pump();

        Check.WithCustomMessage($"[{edge}, pauseWhileHidden={pauseWhileHidden}] still paused; diag: {string.Join(" | ", diag)}")
            .That(interaction.CurrentState).IsNotEqualTo(InteractionState.Paused);
        Check.That(sut.StateName).IsEqualTo("Visible");
    }

    // Spec 2026-07-09 §State machine: "Invalid samples reset neither timer" (edge case #5). A blink
    // inside a 400 ms summon run must not restart it — with the band auto-hiding while paused the
    // run is the only gaze path back, so forgiving beats precise. This is a realistic long blink:
    // ~264 ms between valid samples, seven invalid frames in a row.
    [Fact]
    public void BlinkInsideSummonRun_DoesNotResetIt()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        for (var i = 0; i < 4; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 0..99 ms
        for (var i = 0; i < 7; i++) { sut.Feed(new GazePoint(0, 0, t, false), t); t += 33_000; }       // 132..330 ms
        for (var i = 0; i < 4; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 363..462 ms

        Check.WithCustomMessage("summon run was reset by a ~264 ms blink; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(1);
        // The reset line used to print the invalid sample as a position (raw y=0, x=0) — the
        // provider's sentinel reading like a real jump to the top-left corner.
        Check.WithCustomMessage("the blink was logged as a position; diag: " + string.Join(" | ", diag))
            .That(diag.Any(line => line.Contains("raw y=0, x=0"))).IsFalse();
    }

    // The lower side of the cap: a gap longer than a blink but still inside 400 ms must NOT end
    // the run. This is what raises the cap above the old 300 ms — the longest invalid stretch in
    // the user's 443 s recording is 302 ms, and it must keep its summon.
    [Fact]
    public void LongBlinkWithinTheCap_StillSummons()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        for (var i = 0; i < 4; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 0..99 ms
        for (var i = 0; i < 10; i++) { sut.Feed(new GazePoint(0, 0, t, false), t); t += 33_000; }      // 132..429 ms
        for (var i = 0; i < 2; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 462, 495 ms

        // 363 ms between valid samples: inside the cap, so the original run still fires.
        Check.WithCustomMessage("a 363 ms blink wrongly ended the run; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(1);
    }

    // The other half: a gap longer than the cap is a walk-away, not a blink. The first valid sample
    // after it must seed a fresh run — the summon must not fire for a user who was not there for
    // the first 400 ms.
    [Fact]
    public void InvalidGapLongerThanCap_EndsTheSummonRun()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        for (var i = 0; i < 6; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 0..165 ms
        for (var i = 0; i < 13; i++) { sut.Feed(new GazePoint(0, 0, t, false), t); t += 33_000; }       // 198..594 ms
        for (var i = 0; i < 8; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }   // 627..858 ms

        // 462 ms between valid samples: the run that straddled it is dead.
        Check.WithCustomMessage("a 462 ms invalid gap did not end the run; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(0);

        // ...and the fresh run summons only after its own full 400 ms.
        for (var i = 0; i < 6; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }   // 891..1056 ms
        Check.WithCustomMessage("the fresh run never summoned; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(1);
    }

    // Silence is the gap the old first-invalid anchor could not see: a walk-away ends in a
    // TIMED_OUT storm, where no callbacks arrive at all. The gap is now measured from the last
    // valid sample, so 450 ms of nothing ends the run exactly like 450 ms of invalid frames.
    [Fact]
    public void SilenceLongerThanCap_EndsTheSummonRun()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        for (var i = 0; i < 6; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }  // 0..165 ms
        t += 450_000;                                                                                    // 450 ms of no samples
        for (var i = 0; i < 8; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }   // 648..879 ms

        Check.WithCustomMessage("450 ms of silence did not end the run; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(0);

        for (var i = 0; i < 6; i++) { sut.Feed(new GazePoint(1920, -100, t, true), t); t += 33_000; }   // 912..1077 ms
        Check.WithCustomMessage("the fresh run never summoned; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(1);
    }

    // The cap boundary itself, deliberately fed with no mid-run sample: the gap check is `>` (a gap
    // EQUAL to the cap survives), and the other gap tests all keep their gaps well away from 400 ms.
    [Fact]
    public void GapExactlyAtTheCap_StillSummons()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        sut.Feed(new GazePoint(1920, -100, t, true), t);                      // run starts here
        for (var i = 0; i < 3; i++) { t += 100_000; sut.Feed(new GazePoint(0, 0, t, false), t); }
        t += 100_000;                                                          // 400 ms after the seed
        sut.Feed(new GazePoint(1920, -100, t, true), t);

        Check.WithCustomMessage("a gap exactly at the 400 ms cap ended the run; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(1);
    }

    [Fact]
    public void GapJustOverTheCap_DoesNotSummon()
    {
        var sut = HiddenController(out var calls, out var diag);

        long t = 0;
        sut.Feed(new GazePoint(1920, -100, t, true), t);                      // run starts here
        t += 401_000;                                                          // nothing in between
        sut.Feed(new GazePoint(1920, -100, t, true), t);

        Check.WithCustomMessage("a 401 ms gap did not end the run; diag: " + string.Join(" | ", diag))
            .That(calls.Shows).IsEqualTo(0);
    }

    private static ToolbarAutoHideController HiddenController(out CountingVisibility calls, out List<string> diag)
    {
        var sut = new ToolbarAutoHideController(true, "Top", W, H, new PhysicalRect(920, 0, 2000, 272), 5000, 400, 50);
        calls = new CountingVisibility();
        sut.Attach(calls);
        sut.HideForTest(calls);
        diag = new List<string>();
        sut.AttachDiagnostics(diag.Add);
        return sut;
    }

    private sealed class CountingVisibility : IToolbarVisibility
    {
        public int Shows;
        public void ShowToolbar() => Shows++;
        public void HideToolbar(bool paused) { }
        public void ForceShow() { }
        public void PositionIndicator(string edge) { }
        public void SetPauseIndicator(bool visible) { }
        public void SetActive(bool active) { }
    }
}
