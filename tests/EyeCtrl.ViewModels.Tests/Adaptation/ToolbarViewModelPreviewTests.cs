namespace EyeCtrl.ViewModels.Tests.Adaptation;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Adaptation;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// Task 6: the <see cref="ResetPreviewEngine"/> wired into <see cref="ToolbarViewModel"/> — the
/// feed switch (FeedAll/FeedExcluded/FeedNone around the detector Update), the progress routing
/// (Propagate/HoldAtFreeze/Zero), the activation/deactivation/absent-report resets, the bounds
/// message registration, and the <c>ResetFrozenSilent</c> rim-cue property. Every driven gaze
/// sample carries an explicit <c>TimestampMicroseconds</c> (P4) — no test sleeps.
///
/// Geometry: ToolbarButtonDwellMs defaults to 700 ms, the physical hit radius to 60 px at scale
/// 1.0. Reset sits at (500,500), Pause at (100,100) — 566 px apart, so no sample is ever inside
/// both. Away = (1000,300) is off every target. PanelRect's top-left (540,470) is 50 px from
/// Reset (inside the base circle); the grown-overlap test grows Reset by +40 px so points past
/// the base radius still hit-test true.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ToolbarViewModelPreviewTests
{
    private const string ResetId = ResetPreviewEngine.ResetId;

    private static readonly PhysicalPoint ResetCenter = new(500, 500);
    private static readonly PhysicalPoint PauseCenter = new(100, 100);
    private static readonly PhysicalPoint Away = new(1000, 300);

    // The reported panel: sits right/below of Reset, its top-left corner inside the grown hit
    // area (but past the 60 px base radius at (560,480), 63 px out).
    private static readonly PhysicalRect PanelRect = new(540, 470, 200, 130);

    private readonly EyeCtrlSettings _settings = new();

    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    /// <summary>Captures formatted log entries so the D14 timeout-fallback line can be pinned.</summary>
    private sealed class SpyLogger : ILogger<ToolbarViewModel>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private ToolbarButtonViewModel ResetButton(ToolbarViewModel vm) =>
        vm.Buttons.Single(b => b.Id == ResetId);

    private void PositionCoreButtons(ToolbarViewModel vm)
    {
        vm.UpdateButtonScreenPosition("Pause", PauseCenter);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);
    }

    /// <summary>Drives gaze on Reset just past the 0.40 freeze threshold (0.5 × the 700 ms dwell).</summary>
    private static void DriveToFreeze(ToolbarViewModel vm) =>
        DwellDrive.Hold(0, 350_000, t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));

    // --- rev-1: 0.40 hold — the arc freezes, the panel is requested, nothing fires ---

    [Fact]
    public void DwellPastThreshold_FreezesArc_ShowsPanel_NeverFires()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        // Past the full 700 ms dwell time (without the freeze this would fire Reset) but under
        // the 500 ms-after-engage bounds timeout, which has its own test below.
        DwellDrive.Hold(0, 780_000, t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));

        Check.That(resets).IsEqualTo(0);
        Check.That(visibility).HasSize(1);
        Check.That(visibility[0].Visible).IsTrue();
        Check.That(visibility[0].EngagementId).IsEqualTo(1);
        Check.That(visibility[0].ResetCenter).IsEqualTo(ResetCenter);

        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.DwellProgress).IsStrictlyLessThan(0.60);   // held, not run to 1.0
        Check.That(btn.IsHighlighted).IsTrue();
        Check.That(vm.ResetFrozenSilent).IsFalse();
    }

    // --- rev-1: geometry-fill overlap + panel-top-left — zero activations, zero outcomes ---

    [Fact]
    public void GrownOverlap_PanelTopLeft_IsNotFed_NoActivations_NoOutcomes()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int modes = 0, resets = 0;
        messenger.Register<ModesMenuToggledMessage>(this, (_, _) => modes++);
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);

        var sink = new RecordingSink();
        var vm = new PreviewToolbarViewModel(_settings, messenger, sink);
        PositionCoreButtons(vm);
        // Modes sits INSIDE the panel rect, 71 px from Reset — nearest for the panel-top-left
        // gaze but outside Reset's base circle. Growth makes that gaze point hit Reset too.
        vm.UpdateButtonScreenPosition("Modes", new PhysicalPoint(555, 475));
        vm.GrowTarget(ResetId, 40); // 60 → 100 px effective

        DriveToFreeze(vm);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));
        Check.That(visibility).HasSize(1);

        // Park at (560,480): inside the panel, inside the GROWN Reset hit area (63 px), nearest
        // Modes. FeedNone means the detector is not fed AT ALL — Modes cannot accumulate, Reset
        // cannot fire, and no abandon outcome is reported. A mis-wired FeedExcluded would fire
        // Modes after 700 ms (the filter only excludes Reset); a FeedAll would advance Reset.
        var topLeft = new PhysicalPoint(560, 480);
        DwellDrive.Hold(360_000, 1_800_000, t => vm.Receive(new GazeCursorMovedMessage(topLeft, t)));

        Check.That(modes).IsEqualTo(0);
        Check.That(resets).IsEqualTo(0);
        Check.That(sink.Outcomes).IsEmpty();
        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);   // panel never hidden
        Check.That(ResetButton(vm).IsHighlighted).IsTrue();          // the read holds the arc
    }

    // --- rev-1: 130 ms leave arms; the held arc falls through the real event flow ---

    [Fact]
    public void DebouncedLeave_130ms_HidesPanel_ThenZeroesTheHeldArc()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));

        // The leave run: the detector's Reset(0) on the first off-union sample must NOT drop the
        // arc (P6) — the run may still cancel back into the union.
        vm.Receive(new GazeCursorMovedMessage(Away, 380_000));
        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.IsHighlighted).IsTrue();

        // 140 ms of continuous off-union time ≥ the 120 ms debounce → armed, panel hidden. The
        // drop is driven through the REAL event flow (final review F1): the leave's completion is
        // the last Reset progress-related event of the engagement — the detector cleared Reset's
        // state on the leave run's first excluded sample and gaze is off the button, so no further
        // TargetProgress(ResetId, …) ever arrives to route through the engine's Zero verdict.
        DwellDrive.Hold(410_000, 520_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
        Check.That(visibility[1].EngagementId).IsEqualTo(1);

        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
    }

    // --- final review F1: post-arm with gaze elsewhere and NO further Reset progress, the arc
    // must not linger on the button (floating/Mouse toolbars never deactivate to reset it) ---

    [Fact]
    public void Armed_GazeElsewhere_NoFurtherResetProgress_ArcIsZeroAndStaysZero()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));

        // The production stream, verbatim: first off-union sample, then the debounced leave — no
        // SimulateTargetProgress shortcuts. After arming the detector never emits Reset progress
        // again (its state was cleared by the leave run's excluded updates; gaze is elsewhere).
        vm.Receive(new GazeCursorMovedMessage(Away, 380_000));
        DwellDrive.Hold(410_000, 640_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
        var btn = ResetButton(vm);
        Check.WithCustomMessage(
                "The armed leave's visibility-false must itself drop the held arc — nothing else " +
                "ever will (no further Reset progress event exists post-arm).")
            .That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // And it STAYS down: seconds of gaze elsewhere — no lingering highlight, no re-freeze,
        // no phantom re-show (the engine still holds its Armed pass window this whole time).
        DwellDrive.Hold(670_000, 2_000_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);
    }

    // --- rev-1: the deliberate return fires unfrozen ---

    [Fact]
    public void ArmedReturn_DwellsToFire_SendsResetAdaptationMessage_ThenRepreviews()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));
        DwellDrive.Hold(380_000, 520_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t))); // armed

        // Return within the 3 s window: every progress propagates — the dwell runs to the fire.
        long dwellUs = _settings.Dwell.ToolbarButtonDwellMs * 1000L;
        DwellDrive.Hold(550_000, 550_000 + dwellUs,
            t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));

        Check.That(resets).IsEqualTo(1);
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false); // no 2nd preview mid-pass

        // A later rest previews FRESH: a new engagement, not a zombie pass.
        DwellDrive.Hold(1_700_000, 2_100_000, t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true);
        Check.That(visibility[2].EngagementId).IsEqualTo(2);
        Check.That(resets).IsEqualTo(1);                            // and it must not have fired
    }

    // --- rev-1: SetRenderedButtons absent-report clears target + latch + arc ---

    [Fact]
    public void SetRenderedButtons_ResetAbsent_RemovesTarget_ClearsEngineAndArc()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);

        vm.SetRenderedButtons(new[] { "Pause" });                    // Reset left the band

        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false); // panel retracted
        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // The target is gone: resting where Reset used to be cannot fire it…
        DwellDrive.Hold(400_000, 1_800_000,
            t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));
        Check.That(resets).IsEqualTo(0);

        // …and the engine is disarmed: a BELOW-threshold progress propagates untouched (a fresh
        // 0.45 would just start a new engagement — the engine tests' probe technique).
        vm.SimulateTargetProgress(ResetId, 0.39);
        Check.That(btn.DwellProgress).IsEqualTo(0.39);
        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);   // no new engagement

        // Pause survived the absent-report: it still dwells and fires.
        int pauses = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => pauses++);
        long dwellUs = _settings.Dwell.ToolbarButtonDwellMs * 1000L;
        DwellDrive.Hold(1_900_000, 1_900_000 + dwellUs,
            t => vm.Receive(new GazeCursorMovedMessage(PauseCenter, t)));
        Check.That(pauses).IsEqualTo(1);
    }

    // --- rev-1: bounds never arrive — after the 500 ms timeout the strip still works ---

    [Fact]
    public void BoundsNeverArrive_TimeoutCancelsPanel_PauseStillCompletesAndFires()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int pauses = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => pauses++);
        var logger = new SpyLogger();
        var vm = new ToolbarViewModel(_settings, messenger, logger: logger);
        PositionCoreButtons(vm);
        var resetBtn = ResetButton(vm);

        DriveToFreeze(vm);                                           // engage at ~300 ms, bounds unknown
        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);

        // Gaze straight to Pause. The unknown-bounds guard holds the engagement Frozen (no leave
        // run), but FeedExcluded still feeds every OTHER target — Pause accumulates throughout.
        var midWindowCaptured = false;
        long t = 380_000;
        for (; t <= 1_100_000; t += 30_000)
        {
            vm.Receive(new GazeCursorMovedMessage(PauseCenter, t));

            // 500 ms of sample time after the ~300 ms engage: the panel is cancelled to the
            // hit-area fallback and the rim cue comes on — captured between the timeout (800 ms)
            // and the arm (920 ms), while Pause is still mid-dwell. First sample in that window,
            // so a cadence change cannot silently skip the check.
            if (!midWindowCaptured && t >= 830_000)
            {
                midWindowCaptured = true;
                Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
                Check.That(vm.ResetFrozenSilent).IsTrue();
                // F1 residual: mid-window the leave run is under way but the arming has NOT
                // happened — the arc is still held (the timeout's early hide must not drop it).
                Check.That(resetBtn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
            }
        }

        Check.That(midWindowCaptured).IsTrue();                       // the window really was observed

        Check.That(pauses).IsEqualTo(1);                             // the strip never died
        Check.That(vm.ResetFrozenSilent).IsFalse();                  // rim cue fell at arming

        // F1 residual: the rim cue is not the only thing that fell at arming — this silent
        // engagement publishes no visibility-false at the leave's completion (the timeout's
        // early hide is the only one), so the held arc must fall with it.
        Check.That(resetBtn.DwellProgress).IsEqualTo(0.0);
        Check.That(resetBtn.IsHighlighted).IsFalse();

        // The D14 carry (Task-5 review): the timeout fallback is logged in the VM — one
        // Information line for the hide that arrived with no bounds ever reported.
        Check.That(logger.Entries.Where(e => e.Level == LogLevel.Information)).HasSize(1);
    }

    // --- P15: suppressed at engage — no panel ever existed, the rim cue is the only evidence ---

    [Fact]
    public void SuppressedAtEngage_NoPanel_RimCuePropertyTrue()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        bool suppress = true;
        var vm = new ToolbarViewModel(_settings, messenger, isFeedbackSuppressed: () => suppress);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);

        Check.That(vm.ResetFrozenSilent).IsTrue();
        Check.That(visibility).IsEmpty();                            // no panel ever existed
        Check.That(ResetButton(vm).DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);

        // L1: suppression ending restores nothing — and the silent read still leaves normally.
        suppress = false;
        DwellDrive.Hold(380_000, 520_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));
        Check.That(visibility).IsEmpty();                            // still nothing: no phantom hide
        Check.That(vm.ResetFrozenSilent).IsFalse();
    }

    // --- final review F1 residual: the panel-absent sibling of Armed_GazeElsewhere above. A
    // suppressed-at-engage run arms with NO visibility-false (the panel never existed), so the
    // held arc must fall through the FrozenSilentChanged-at-arming flow instead — nothing else
    // ever will (no further Reset progress event exists post-arm) ---

    [Fact]
    public void SuppressedAtEngage_DebouncedLeave_Arms_ArcIsZeroAndStaysZero()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        bool suppress = true;
        var vm = new ToolbarViewModel(_settings, messenger, isFeedbackSuppressed: () => suppress);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.IsHighlighted).IsTrue();
        Check.That(vm.ResetFrozenSilent).IsTrue();

        // L1: suppression ending restores nothing. The silent read leaves through the production
        // stream, verbatim — first off-union sample (P6: the arc must hold through the run),
        // then the debounced leave. No SimulateTargetProgress shortcuts.
        suppress = false;
        vm.Receive(new GazeCursorMovedMessage(Away, 380_000));
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        DwellDrive.Hold(410_000, 640_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        // The whole engagement published NOTHING — so the arc must still have fallen at arming.
        Check.That(visibility).IsEmpty();
        Check.That(vm.ResetFrozenSilent).IsFalse();
        Check.WithCustomMessage(
                "A silent engagement's leave completion publishes no visibility-false — the arc " +
                "must fall when FrozenSilent falls with the engine Armed, or it lingers forever.")
            .That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // And it STAYS down: seconds of gaze elsewhere, still inside the Armed pass window.
        DwellDrive.Hold(670_000, 2_000_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
    }

    // --- final review F1 residual, the NoPanel-at-show shape (floating/Mouse toolbars — the
    // every-engagement path there): the service's absence answer publishes an EARLY
    // visibility-false while still Frozen (arc held, the user may still be reading), and the
    // leave then arms with the panel long down — the drop must come at arming, not at the hide ---

    [Fact]
    public void NoPanelAtShow_EarlyHideHoldsArc_LeaveArmZeroesIt()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        var btn = ResetButton(vm);

        // The floating toolbar's answer: nothing is or will be shown. The engine retracts its
        // own show request — a visibility-false while still Frozen, so the arc must HOLD (the
        // engagement is live and the reader may still be on the button).
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.NoPanel, null));

        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
        Check.That(vm.ResetFrozenSilent).IsTrue();
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.IsHighlighted).IsTrue();

        // The leave: first off-union sample (P6 hold), then the debounce arms.
        vm.Receive(new GazeCursorMovedMessage(Away, 380_000));
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        DwellDrive.Hold(410_000, 640_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(visibility).HasSize(2);                    // no second hide: the panel was already down
        Check.That(vm.ResetFrozenSilent).IsFalse();
        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // And it stays down.
        DwellDrive.Hold(670_000, 2_000_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));
        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
    }

    // --- final review F1 residual, the D17 policy-hide shape: HiddenKeepLast mid-read keeps the
    // last rect in the union (no cancel published), and the leave then arms with the panel long
    // down — again the drop must come at arming, through the FrozenSilentChanged flow ---

    [Fact]
    public void HiddenKeepLast_MidRead_LeaveArms_ArcIsZeroAndStaysZero()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));
        var btn = ResetButton(vm);

        // Mid-read inside the panel, the service policy-hides it (D17): no cancel echo, the last
        // rect STAYS in the union so the hide cannot arm the button under the reader's gaze.
        DwellDrive.Hold(360_000, 500_000,
            t => vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(560, 480), t)));
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.HiddenKeepLast, PanelRect));

        Check.That(visibility.Count(v => v.Visible)).IsEqualTo(1);   // the policy hide published nothing
        Check.That(vm.ResetFrozenSilent).IsTrue();
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);

        // The leave: first off-union sample (P6 hold), then the debounce arms.
        vm.Receive(new GazeCursorMovedMessage(Away, 530_000));
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        DwellDrive.Hold(560_000, 700_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(visibility).HasSize(1);                    // no hide ever: the panel was down at the leave
        Check.That(vm.ResetFrozenSilent).IsFalse();
        Check.WithCustomMessage(
                "A policy-hidden engagement's leave completion publishes no visibility-false — the " +
                "arc must fall when FrozenSilent falls with the engine Armed.")
            .That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // And it stays down, still inside the Armed pass window.
        DwellDrive.Hold(730_000, 2_200_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t)));

        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
    }

    // --- D9's abandon-outcome filter gate (fix-wave F4): the `targetFilter is null ||
    // targetFilter(target)` condition on DwellDetector's abandon arm is what gives FeedExcluded's
    // filter its telemetry teeth — an excluded target must not surface struggle outcomes from
    // samples taken in the preview's area of influence. Removing the gate passed every existing
    // suite (mutant X1); this is the load-bearing probe. ---

    [Fact]
    public void LeaveRunFirstSample_FilteredResetReportsNoAbandonOutcome()
    {
        var messenger = new WeakReferenceMessenger();
        var sink = new RecordingSink();
        var vm = new PreviewToolbarViewModel(_settings, messenger, sink);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);   // samples at 30 ms cadence; the freeze engages at t=300 ms, so the
                             // 330/350 ms samples on Reset are FeedNone — the detector is last FED
                             // at t=300 ms with Reset holding ~0.43 entered dwell
        Check.That(sink.Outcomes).IsEmpty();

        // The very next 30 ms sample off the union, only 80 ms after the last fed sample — under
        // the 150 ms gap clamp, so NO re-anchor: Reset still carries its entered timestamp and the
        // abandon arithmetic would clear the 0.3 AbandonProgress floor (0.38 s / 0.7 s ≈ 0.54).
        // FeedExcluded feeds the detector with Reset filtered out; with the gate present the
        // cleared dwell stays silent — without it (X1) the excluded Reset would report an
        // Abandoned outcome from inside the preview's area of influence.
        vm.Receive(new GazeCursorMovedMessage(Away, 380_000));

        Check.WithCustomMessage(
                "FeedExcluded's filter must gate the abandon outcome too: Reset's cleared dwell " +
                "must not surface as a struggle Abandoned outcome.")
            .That(sink.Outcomes).IsEmpty();
    }

    // --- deactivation: hide/calibration closes the engagement and drops the held arc ---

    [Fact]
    public void SetActive_False_RetractsPanel_AndLetsTheHeldArcFall()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);

        vm.SetActive(false);

        // The engine reset retracts the shown panel…
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
        // …and because it runs BEFORE ResetAllState, the Reset(0) the detector fires on reset
        // routes through a DISARMED engine and the arc falls — instead of Holding at the freeze.
        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();

        // Re-activation previews fresh: no zombie engagement survives the hide.
        vm.SetActive(true);
        DwellDrive.Hold(400_000, 750_000, t => vm.Receive(new GazeCursorMovedMessage(ResetCenter, t)));
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true);
        Check.That(visibility[2].EngagementId).IsEqualTo(2);
    }

    // --- anchor follow: a re-docked Reset re-anchors the NEXT engagement's publish ---

    [Fact]
    public void RepositionedReset_SecondEngagement_PublishesNewAnchor()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        var vm = new ToolbarViewModel(_settings, messenger);
        PositionCoreButtons(vm);

        DriveToFreeze(vm);
        Check.That(visibility[0].ResetCenter).IsEqualTo(ResetCenter);
        messenger.Send(new AdaptationPreviewBoundsMessage(1, PreviewBoundsKind.Rect, PanelRect));

        var moved = new PhysicalPoint(700, 300);
        vm.UpdateButtonScreenPosition(ResetId, moved);
        DwellDrive.Hold(380_000, 520_000, t => vm.Receive(new GazeCursorMovedMessage(Away, t))); // armed

        // Return only past the 3 s arm expiry, so this rest previews fresh (a return inside the
        // window would fire unfrozen — that path is the ArmedReturn test).
        DwellDrive.Hold(3_650_000, 4_400_000, t => vm.Receive(new GazeCursorMovedMessage(moved, t)));
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true);
        Check.That(visibility[2].ResetCenter).IsEqualTo(moved);
    }
}

/// <summary>
/// Exposes the detector so a test can apply the adaptation geometry margin production applies
/// through <c>IDwellDetectorRegistry</c>/<c>GeometryRouter</c> (same rationale as
/// <c>ProbeToolbarViewModel</c> in the sibling suite). Top-level and internal, not nested and
/// private: the MVVM Toolkit's RegisterAll source generator must be able to see any subclass of
/// an <c>IRecipient{T}</c> type.
/// </summary>
internal sealed class PreviewToolbarViewModel : ToolbarViewModel
{
    public PreviewToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, Func<bool>? isFeedbackSuppressed = null)
        : base(settings, messenger, sink, isFeedbackSuppressed: isFeedbackSuppressed) { }

    public void GrowTarget(string id, double marginPx) => DwellDetector.UpdateTargetGeometry(id, marginPx);
}
