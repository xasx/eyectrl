namespace EyeCtrl.ViewModels.Tests.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Adaptation;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// ResetPreviewEngine contract tests (seen-latch freeze, Tasks 4–5). Everything is driven by
/// explicit sample timestamps in microseconds (P4) — the engine has no clock of its own, so no
/// test sleeps or reads the wall clock. States under test: Disarmed → Frozen → Leaving → Armed,
/// with Threshold = 0.40, LeaveDebounceUs = 120 ms, ArmExpiryUs = 3 s, BoundsTimeoutUs = 500 ms.
/// The Task-4 mechanics tests drive the engine with a KNOWN panel rect (OnBounds(Rect) after
/// engage) so the debounce runs are not held by the unknown-bounds guard; the Task-5 tests pin
/// the guard and the three-kind bounds protocol itself.
/// </summary>
public class ResetPreviewEngineTests
{
    private const string ResetId = ResetPreviewEngine.ResetId;

    // The engine trusts `gazeOverReset` for the hit area; OnReset/Away only need to be distinct,
    // realistic physical points. PanelRect sits below the button (anchored at 960,540): it holds
    // InPanel but neither OnReset nor Away, so off-union samples stay off-union under it.
    private static readonly PhysicalPoint Anchor = new(960, 540);
    private static readonly PhysicalPoint OnReset = new(960, 540);
    private static readonly PhysicalPoint Away = new(960, 300);
    private static readonly PhysicalRect PanelRect = new(860, 560, 200, 120);
    private static readonly PhysicalRect MovedPanelRect = new(860, 700, 200, 120);
    private static readonly PhysicalPoint InPanel = new(960, 600);   // inside PanelRect only

    private sealed class Harness
    {
        public ResetPreviewEngine Engine { get; }
        public List<(bool Visible, int Id, PhysicalPoint Anchor)> Visibility { get; } = new();
        public int SilentChangedCount { get; private set; }
        public bool Suppress { get; set; }

        public Harness(bool suppress = false)
        {
            Engine = new ResetPreviewEngine(() => Suppress);
            Engine.PreviewVisibility += (visible, id, anchor) => Visibility.Add((visible, id, anchor));
            Engine.FrozenSilentChanged += () => SilentChangedCount++;
            Suppress = suppress;
            Engine.SetAnchor(Anchor);
        }

        public int ShownCount => Visibility.Count(v => v.Visible);
        public int HiddenCount => Visibility.Count(v => !v.Visible);
    }

    // --- rev-1: engagement at 0.40 / 0.39 ---

    [Fact]
    public void Engage_AtExactThreshold_Freezes_PublishesPanelWithAnchor()
    {
        var h = new Harness();

        var got = h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);

        Check.That(got).IsEqualTo(PreviewProgress.HoldAtFreeze);   // the engaging event already crossed: freeze at it
        Check.That(h.Visibility).HasSize(1);
        var (visible, id, anchor) = h.Visibility[0];
        Check.That(visible).IsTrue();
        Check.That(id).IsEqualTo(1);
        Check.That(anchor).IsEqualTo(Anchor);
        Check.That(h.Engine.FrozenSilent).IsFalse();
        Check.That(h.SilentChangedCount).IsEqualTo(0);             // unsuppressed: no rim-cue flicker
    }

    [Fact]
    public void Engage_BelowThreshold_DoesNotEngage()
    {
        // Also covers the null-suppression-delegate default of the constructor.
        var engine = new ResetPreviewEngine();
        engine.SetAnchor(Anchor);
        var events = 0;
        engine.PreviewVisibility += (_, _, _) => events++;

        Check.That(engine.OnTargetProgress(ResetId, 0.39, 100_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(events).IsEqualTo(0);
        Check.That(engine.FrozenSilent).IsFalse();
        Check.That(engine.IsArmed).IsFalse();
        Check.That(engine.DecideFeed(true, OnReset, 150_000)).IsEqualTo(PreviewFeed.FeedAll);
    }

    // --- rev-1: panel → button → panel no-fire ---

    [Fact]
    public void PanelButtonPanelShuttle_NeverArms_NeverFires()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds — the debounce runs are real

        // Shuttle panel (off the button) → button → panel → button; every off-run is far below
        // the 120 ms debounce, so the runs cancel and the engine never arms — and only an armed
        // engine can let a later rest fire.
        Check.That(h.Engine.DecideFeed(false, Away, 130_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // run starts
        Check.That(h.Engine.DecideFeed(true, OnReset, 160_000)).IsEqualTo(PreviewFeed.FeedNone);    // cancel → Frozen
        Check.That(h.Engine.DecideFeed(false, Away, 190_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(true, OnReset, 220_000)).IsEqualTo(PreviewFeed.FeedNone);

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.HiddenCount).IsEqualTo(0);
        // Resting back on the button re-freezes instead of firing: the arc is still held.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.80, 260_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
    }

    // --- rev-1: HoldAtFreeze arc + highlight contract via return values ---

    [Fact]
    public void Frozen_HoldsArcAndHighlight_AnyProgressReturnsHoldAtFreeze()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);

        Check.That(h.Engine.OnTargetProgress(ResetId, 0.55, 200_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        // Even the detector's reset-to-zero must not drop the arc while frozen: the highlight
        // contract rides on the same value — Zero would blank both.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 300_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        // Other targets stay live while Reset is frozen.
        Check.That(h.Engine.OnTargetProgress("Modes", 0.90, 310_000)).IsEqualTo(PreviewProgress.Propagate);
    }

    // --- rev-1: multi-second read, no hide, no fire ---

    [Fact]
    public void MultiSecondRead_OverReset_NoHideNoFire()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds

        for (long t = 150_000; t <= 3_600_000; t += 50_000)
        {
            Check.That(h.Engine.DecideFeed(true, OnReset, t)).IsEqualTo(PreviewFeed.FeedNone);
            Check.That(h.Engine.OnTargetProgress(ResetId, 0.42, t)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        }

        Check.That(h.ShownCount).IsEqualTo(1);
        Check.That(h.HiddenCount).IsEqualTo(0);
        Check.That(h.SilentChangedCount).IsEqualTo(0);
        Check.That(h.Engine.IsArmed).IsFalse();
    }

    // --- rev-1: 1- and 3-sample excursions don't arm ---

    [Fact]
    public void SingleSampleExcursion_DoesNotArm()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds

        Check.That(h.Engine.DecideFeed(false, Away, 140_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // run starts
        Check.That(h.Engine.DecideFeed(true, OnReset, 170_000)).IsEqualTo(PreviewFeed.FeedNone);    // 30 ms: cancel

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.HiddenCount).IsEqualTo(0);
    }

    [Fact]
    public void ThreeSampleExcursion_DoesNotArm()
    {
        // Three off samples at a 30 ms cadence span only 60 ms — the debounce needs 120 ms of
        // CONTINUOUS off-union time, not three frames of it.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds

        foreach (var t in new[] { 120_000L, 150_000L, 180_000L })
            Check.That(h.Engine.DecideFeed(false, Away, t)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(true, OnReset, 210_000)).IsEqualTo(PreviewFeed.FeedNone);

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.HiddenCount).IsEqualTo(0);
    }

    // --- rev-1 (P6-corrected): debounced leave arms + Zero is only the post-arm drop ---

    [Fact]
    public void DebouncedLeave_Arms_HidesPanel_ZeroOnlyAfterArm()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds

        Check.That(h.Engine.DecideFeed(false, Away, 100_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // run starts
        // P6: the leave run's ungated TargetProgress(0) holds the arc — Zero is NOT returned here.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 150_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.Engine.DecideFeed(false, Away, 160_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 220_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // 120 ms → arms

        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(1);
        Check.That(h.Visibility[^1].Id).IsEqualTo(1);
        Check.That(h.Engine.FrozenSilent).IsFalse();
        // The post-arm drop: only now may the held 0.40 arc fall to zero.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 260_000)).IsEqualTo(PreviewProgress.Zero);
    }

    // --- rev-1: expiry at 3.1 s re-previews ---

    [Fact]
    public void ReturnAfterArmExpiry_At3_1s_RepreviewsWithFreshEngagement()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                 // armed at 220_000
        Check.That(h.Engine.IsArmed).IsTrue();

        // First Reset progress of the gaze return arrives 3.1 s after arming → Disarmed first,
        // then this same event previews fresh (a new engagement id, a new panel).
        var got = h.Engine.OnTargetProgress(ResetId, 0.41, 220_000 + 3_100_000);

        Check.That(got).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.ShownCount).IsEqualTo(2);
        Check.That(h.Visibility[^1].Id).IsEqualTo(2);
        Check.That(h.Engine.IsArmed).IsFalse();
    }

    // --- rev-1: 2.9 s return fires unfrozen with no second visibility-true ---

    [Fact]
    public void ReturnWithin2_9s_FiresUnfrozen_NoSecondPreview()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                 // armed at 220_000

        Check.That(h.Engine.OnTargetProgress(ResetId, 0.20, 220_000 + 2_900_000)).IsEqualTo(PreviewProgress.Propagate);
        // Past 0.40 mid-pass: no freeze engages — the deliberate re-dwell runs to the fire.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.60, 220_000 + 3_400_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.OnTargetProgress(ResetId, 1.00, 220_000 + 3_900_000)).IsEqualTo(PreviewProgress.Propagate);
        h.Engine.OnActivated(ResetId);                              // the destructive fire

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.ShownCount).IsEqualTo(1);                      // no second visibility-true
    }

    // --- P3 (L2, pinned times): overlap where HitTest(Reset) is true but Modes accumulates/fires ---

    [Fact]
    public void P3_Overlap_ModesFires_LateRestKeepsOriginalArmTime_Repreviews()
    {
        // Pass-end → Armed-original-timestamp rule: OnActivated("Modes") clears only the pass and
        // leaves _armedAtUs at the ORIGINAL arm time, so expiry is measured from 220_000.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                  // armed at 220_000
        Check.That(h.Engine.IsArmed).IsTrue();

        // Grown-overlap: HitTest(Reset) true while Modes is nearest and accumulates. Reset itself
        // never emits progress, so no pass ever starts here.
        Check.That(h.Engine.DecideFeed(true, OnReset, 300_000)).IsEqualTo(PreviewFeed.FeedAll);
        Check.That(h.Engine.OnTargetProgress("Modes", 0.50, 400_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.OnTargetProgress("Modes", 1.00, 500_000)).IsEqualTo(PreviewProgress.Propagate);
        h.Engine.OnActivated("Modes");                              // pass (never armed) ends; Armed keeps 220_000
        Check.That(h.Engine.IsArmed).IsTrue();

        // Rest on Reset 3.1 s after the ORIGINAL arming → previews again, no zombie pass. (Had the
        // Modes activation moved the arm clock to 500_000, this event at 3_320_000 would be only
        // 2.82 s in — within expiry — and would fire unfrozen instead of previewing.)
        var got = h.Engine.OnTargetProgress(ResetId, 0.41, 3_320_000);

        Check.That(got).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.ShownCount).IsEqualTo(2);
        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.50, 3_400_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
    }

    [Fact]
    public void P3_Overlap_ModesFires_EarlyRest_FiresUnfrozen_LatchPersistsThroughOtherActivation()
    {
        // Sibling: the same overlap → Modes-fires sequence, but the later rest is WITHIN 3 s of
        // the original arming — the latch legitimately persists through another target's
        // activation, so the return fires unfrozen with no second preview.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                  // armed at 220_000

        Check.That(h.Engine.DecideFeed(true, OnReset, 300_000)).IsEqualTo(PreviewFeed.FeedAll);
        h.Engine.OnTargetProgress("Modes", 1.00, 500_000);
        h.Engine.OnActivated("Modes");

        // Rest within 3 s of arming (1.48 s in): pass arms and runs to the fire.
        Check.That(h.Engine.DecideFeed(true, OnReset, 1_650_000)).IsEqualTo(PreviewFeed.FeedAll);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.20, 1_700_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.60, 2_200_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.OnTargetProgress(ResetId, 1.00, 2_700_000)).IsEqualTo(PreviewProgress.Propagate);
        h.Engine.OnActivated(ResetId);

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.ShownCount).IsEqualTo(1);
    }

    // --- P6: excursion cancelled mid-run — the arc still reads the held 0.40 ---

    [Fact]
    public void P6_ExcursionCancelledMidRun_ArcStillReadsHeldThreshold()
    {
        var h = new Harness();
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.40, 100_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds

        h.Engine.DecideFeed(false, Away, 120_000);                  // run starts
        // The detector resets while the gaze is away — the run must NOT forward the drop.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 140_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        h.Engine.DecideFeed(true, OnReset, 160_000);                // cancel back into the union → Frozen

        // No Zero was ever returned, so the arc still reads the held 0.40, and further progress
        // holds it there rather than resuming from the dropped value.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.30, 200_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
    }

    // --- P18: state committed before any event is raised (re-entrant subscriber) ---

    [Fact]
    public void P18_ReentrantOnBoundsFromVisibilitySubscriber_StateSurvives()
    {
        var engine = new ResetPreviewEngine();
        engine.SetAnchor(Anchor);
        var reEntries = 0;
        engine.PreviewVisibility += (visible, id, anchor) =>
        {
            // Task 5's API: a PreviewVisibility subscriber may synchronously report bounds from
            // inside the event — the engine must already be in its committed state when this runs.
            engine.OnBounds(id, PreviewBoundsKind.Rect, new PhysicalRect(900, 500, 120, 80));
            reEntries++;
        };

        engine.OnTargetProgress(ResetId, 0.40, 100_000);            // visibility-true raises re-entrantly
        Check.That(reEntries).IsEqualTo(1);
        Check.That(engine.FrozenSilent).IsFalse();
        Check.That(engine.DecideFeed(true, OnReset, 150_000)).IsEqualTo(PreviewFeed.FeedNone);       // still Frozen
        Check.That(engine.OnTargetProgress(ResetId, 0.50, 200_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);

        engine.DecideFeed(false, Away, 250_000);
        engine.DecideFeed(false, Away, 380_000);                    // arms → visibility-false re-enters too
        Check.That(reEntries).IsEqualTo(2);
        Check.That(engine.IsArmed).IsTrue();
    }

    // --- P15 suppression: suppressed at engage — no panel ever existed ---

    [Fact]
    public void SuppressedAtEngage_NeverPublishes_UnionIsHitAreaImmediately()
    {
        var h = new Harness(suppress: true);

        var got = h.Engine.OnTargetProgress(ResetId, 0.41, 100_000);

        Check.That(got).IsEqualTo(PreviewProgress.HoldAtFreeze);    // the freeze still protects the button
        Check.That(h.Visibility).IsEmpty();                         // publish NOTHING, ever — no panel existed
        Check.That(h.Engine.FrozenSilent).IsTrue();                 // D18 rim cue: the only evidence of the freeze
        Check.That(h.SilentChangedCount).IsEqualTo(1);

        // Union = hit area from the first sample (no unknown window): the leave run starts on the
        // very first off-button sample and completes on sample time alone.
        Check.That(h.Engine.DecideFeed(false, Away, 120_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 240_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // 120 ms → arms
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.Visibility).IsEmpty();                         // still nothing: no visibility-false for a phantom panel
        Check.That(h.Engine.FrozenSilent).IsFalse();
        Check.That(h.SilentChangedCount).IsEqualTo(2);
    }

    // --- P15/L1 suppression: rising mid-read policy-hides; ending does not restore ---

    [Fact]
    public void SuppressionRisingMidRead_HidesPanel_SuppressionEndDoesNotRestore()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);          // panel up, unsuppressed

        h.Suppress = true;
        Check.That(h.Engine.DecideFeed(true, OnReset, 200_000)).IsEqualTo(PreviewFeed.FeedNone);    // policy hide at the top
        Check.That(h.HiddenCount).IsEqualTo(1);
        Check.That(h.Engine.FrozenSilent).IsTrue();
        Check.That(h.SilentChangedCount).IsEqualTo(1);

        h.Suppress = false;                                         // L1: ending does NOT restore this engagement's panel
        Check.That(h.Engine.DecideFeed(true, OnReset, 260_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.ShownCount).IsEqualTo(1);
        Check.That(h.Engine.FrozenSilent).IsTrue();

        // The frozen-but-silent read still leaves normally, and the rim cue falls at arming.
        Check.That(h.Engine.DecideFeed(false, Away, 300_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 430_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(1);                     // no second hide: the panel was already down
        Check.That(h.Engine.FrozenSilent).IsFalse();
        Check.That(h.SilentChangedCount).IsEqualTo(2);
    }

    // --- P16: worst-case escape bound, all on timestamps ---

    [Fact]
    public void P16_WorstCaseEscape_FreezePlusDebouncePlusFullDwell_FiresUninterrupted()
    {
        // D = 1 s dwell. Worst case: the freeze tax is 0.4×D + the 120 ms leave + a full 1×D
        // re-dwell; the pass must run to the fire with no preview interrupting it.
        var h = new Harness();

        long t = 0;
        for (var i = 1; i <= 8; i++)                                // 0.40 at 400 ms → engage
            h.Engine.OnTargetProgress(ResetId, i * 0.05, t += 50_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        Check.That(h.ShownCount).IsEqualTo(1);

        Check.That(h.Engine.DecideFeed(false, Away, 400_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // run starts
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 460_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);  // P6
        Check.That(h.Engine.DecideFeed(false, Away, 520_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // armed at 520 ms
        Check.That(h.Engine.IsArmed).IsTrue();

        // Return and a FULL dwell — every progress event, including those past 0.40, propagates.
        Check.That(h.Engine.DecideFeed(true, OnReset, 550_000)).IsEqualTo(PreviewFeed.FeedAll);
        var pass = new List<PreviewProgress>();
        for (var i = 1; i <= 20; i++)                               // 1.00 at 1.55 s
            pass.Add(h.Engine.OnTargetProgress(ResetId, i * 0.05, 550_000 + i * 50_000));
        Check.That(pass).ContainsOnlyElementsThatMatch(p => p == PreviewProgress.Propagate);
        Check.That(h.ShownCount).IsEqualTo(1);                      // no preview interrupted the armed pass
        h.Engine.OnActivated(ResetId);                              // fire consumed
        Check.That(h.Engine.IsArmed).IsFalse();

        // Bound: fire completed at 1.55 s = 0.4×D + 120 ms + 1×D (+ the 30 ms return), and a later
        // return restarts WITH a preview — the user is never stuck without one.
        Check.That(h.Engine.DecideFeed(true, OnReset, 3_570_000)).IsEqualTo(PreviewFeed.FeedAll);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.05, 3_570_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.45, 4_020_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.ShownCount).IsEqualTo(2);
    }

    // --- API: another target's activation mid-leave leaves the preview alone ---

    [Fact]
    public void OtherTargetActivation_MidLeave_PanelStaysUp()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 120_000);                  // Leaving, panel still up

        h.Engine.OnActivated("Modes");                              // the gaze dwelled Modes during the debounce

        Check.That(h.Visibility).HasSize(1);                        // nothing published: the preview outlives it
        Check.That(h.Engine.DecideFeed(false, Away, 250_000)).IsEqualTo(PreviewFeed.FeedExcluded);  // 130 ms → arms now
        Check.That(h.HiddenCount).IsEqualTo(1);
    }

    // --- API: Reset() — deactivate / band rebuild / absent-report ---

    [Fact]
    public void Reset_WhilePanelUp_HidesPanel_AndDisarms()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);

        h.Engine.Reset();

        Check.That(h.HiddenCount).IsEqualTo(1);
        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.Engine.FrozenSilent).IsFalse();
        // Probe with a BELOW-threshold value: in Frozen even 0.39 returns HoldAtFreeze, so
        // Propagate here proves the freeze is gone (0.55 would just start a fresh engagement).
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.39, 200_000)).IsEqualTo(PreviewProgress.Propagate);
        Check.That(h.Engine.DecideFeed(true, OnReset, 250_000)).IsEqualTo(PreviewFeed.FeedAll);
    }

    [Fact]
    public void Reset_DuringArmedPass_EndsPass_NextRestPreviews()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                  // armed
        h.Engine.OnTargetProgress(ResetId, 0.20, 3_120_000);        // pass armed (2.9 s in)

        h.Engine.Reset();

        Check.That(h.Engine.IsArmed).IsFalse();
        // The pass is gone: a later 0.40+ rest engages a FRESH preview instead of firing.
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.45, 3_500_000)).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.ShownCount).IsEqualTo(2);
    }

    // --- P2/P10 message shapes (verbatim contract Tasks 10/11 consume) ---

    [Fact]
    public void PreviewMessages_CarryP2Shape()
    {
        var visibility = new AdaptationPreviewVisibilityMessage(true, 3, Anchor);
        Check.That(visibility.Visible).IsTrue();
        Check.That(visibility.EngagementId).IsEqualTo(3);
        Check.That(visibility.ResetCenter).IsEqualTo(Anchor);

        var bounds = new AdaptationPreviewBoundsMessage(3, PreviewBoundsKind.HiddenKeepLast, new PhysicalRect(0, 0, 10, 10));
        Check.That(bounds.EngagementId).IsEqualTo(3);
        Check.That(bounds.Kind).IsEqualTo(PreviewBoundsKind.HiddenKeepLast);
        Check.That(bounds.PanelBounds).IsNotNull();

        var absent = new AdaptationPreviewBoundsMessage(4, PreviewBoundsKind.NoPanel, null);
        Check.That(absent.PanelBounds).IsNull();

        Check.That(Enum.GetNames<PreviewBoundsKind>())
            .IsEqualTo(new[] { "Rect", "HiddenKeepLast", "NoPanel" });
    }

    // -------------------------------------------------------------------------
    // Task 5: the bounds protocol (P2/P11/D14) — Rect store/resize, HiddenKeepLast,
    // NoPanel, the unknown-window guard + 500 ms timeout, id matching, first-show-only.
    // -------------------------------------------------------------------------

    [Fact]
    public void OnBounds_Rect_LastKnownStaysUntilReplacedByLaterRect()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);              // engage, panel wanted
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);        // first show

        // Stored: gaze inside the reported rect (off the button) is in the union → FeedNone.
        Check.That(h.Engine.DecideFeed(false, InPanel, 150_000)).IsEqualTo(PreviewFeed.FeedNone);

        // No new report: the last-known rect persists — the union does not revert to the hit area.
        Check.That(h.Engine.DecideFeed(false, InPanel, 300_000)).IsEqualTo(PreviewFeed.FeedNone);

        // The resize report REPLACES it: the same point, inside the old rect only, falls out.
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, MovedPanelRect);
        Check.That(h.Engine.DecideFeed(false, InPanel, 350_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsFalse();
    }

    [Fact]
    public void OnBounds_HiddenKeepLast_KeepsOldRectInUnion_ReaderHoldingItDoesNotArm()
    {
        // C1 regression: a D17 collision hides the panel while the reader is mid-read. The hide
        // must not arm the button under the reader's gaze — the old rect still shields it.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);

        h.Engine.OnBounds(1, PreviewBoundsKind.HiddenKeepLast, null);   // service policy-hid it
        Check.That(h.Engine.FrozenSilent).IsTrue();                     // engagement live, no panel
        Check.That(h.HiddenCount).IsEqualTo(0);                         // the service hid it: no echo

        // Gaze held on the OLD rect far past the 120 ms debounce: still Frozen, still FeedNone.
        Check.That(h.Engine.DecideFeed(false, InPanel, 200_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.Engine.DecideFeed(false, InPanel, 400_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.Engine.IsArmed).IsFalse();                         // the regression pin

        // The leave debounce itself is unchanged: leaving the old rect entirely arms normally.
        Check.That(h.Engine.DecideFeed(false, Away, 500_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 640_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(0);                         // panel already down: no hide
        Check.That(h.Engine.FrozenSilent).IsFalse();
    }

    [Fact]
    public void SuppressionFlipAfterKnownRect_HidesPanel_OldRectStillShields_DoesNotArm()
    {
        // R2-M2 regression: the engine's own mid-read suppression hide keeps the last rect in the
        // union too — flipping suppression back must neither restore the panel nor arm the button
        // while the reader still rests on the old rect.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);

        h.Suppress = true;
        Check.That(h.Engine.DecideFeed(false, InPanel, 200_000)).IsEqualTo(PreviewFeed.FeedNone);  // policy hide, still in union
        Check.That(h.HiddenCount).IsEqualTo(1);                         // the ENGINE commands this hide
        Check.That(h.Engine.FrozenSilent).IsTrue();

        Check.That(h.Engine.DecideFeed(false, InPanel, 400_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.Engine.IsArmed).IsFalse();                         // > 120 ms on the old rect: no arm

        h.Suppress = false;                                             // L1: ending restores nothing
        Check.That(h.Engine.DecideFeed(false, InPanel, 450_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.ShownCount).IsEqualTo(1);
        Check.That(h.Engine.FrozenSilent).IsTrue();

        Check.That(h.Engine.DecideFeed(false, Away, 500_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 640_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(1);                         // no second hide
    }

    [Fact]
    public void OnBounds_NoPanel_FallsBackToHitArea_Immediately()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);
        Check.That(h.Engine.DecideFeed(false, InPanel, 150_000)).IsEqualTo(PreviewFeed.FeedNone);

        h.Engine.OnBounds(1, PreviewBoundsKind.NoPanel, null);          // nothing was or is shown
        Check.That(h.Engine.FrozenSilent).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(1);                         // the cancel: our show request retracted
        Check.That(h.ShownCount).IsEqualTo(1);

        // The known rect is dropped: the same in-rect point now starts the leave run.
        Check.That(h.Engine.DecideFeed(false, InPanel, 200_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, InPanel, 340_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();                          // hit-area union: Pause still fires (Task 6)
    }

    [Fact]
    public void BoundsNeverArrive_GuardHoldsLeave_Until500MsTimeoutCancelsToFallback()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);              // engage; bounds unknown
        Check.That(h.ShownCount).IsEqualTo(1);

        // Guard: bounds unknown while the panel is wanted — off-button gaze stays Frozen with no
        // leave run, however long it accumulates (400 ms continuous here, > the 120 ms debounce).
        Check.That(h.Engine.DecideFeed(false, Away, 150_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 300_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 500_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.HiddenCount).IsEqualTo(0);

        // At exactly 500 ms of sample time the panel is cancelled and the union is the hit area.
        Check.That(h.Engine.DecideFeed(false, Away, 600_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.HiddenCount).IsEqualTo(1);
        Check.That(h.Visibility[^1].Id).IsEqualTo(1);
        Check.That(h.Engine.FrozenSilent).IsTrue();

        // Post-fallback: on-button FeedNone (union = hit area), off-button runs and arms.
        Check.That(h.Engine.DecideFeed(true, OnReset, 650_000)).IsEqualTo(PreviewFeed.FeedNone);
        Check.That(h.Engine.DecideFeed(false, Away, 700_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, Away, 830_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.Engine.FrozenSilent).IsFalse();
        Check.That(h.SilentChangedCount).IsEqualTo(2);
    }

    [Fact]
    public void LateRectAfterTimeout_DoesNotResurrectCancelledPanel()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);              // bounds never arrive
        h.Engine.DecideFeed(false, Away, 300_000);                      // guarded, no run
        h.Engine.DecideFeed(false, Away, 600_000);                      // timeout cancels the panel
        Check.That(h.HiddenCount).IsEqualTo(1);

        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);        // late report: engagement closed

        // Gaze inside the late rect is NOT in the union — the run continues and completes.
        Check.That(h.Engine.DecideFeed(false, InPanel, 700_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, InPanel, 840_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.ShownCount).IsEqualTo(1);                          // no resurrection, no re-show
    }

    [Fact]
    public void OnBounds_StaleEngagementId_Dropped_StaleRectNotInNewUnion()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);              // engagement 1
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);
        h.Engine.Reset();                                               // close engagement 1
        Check.That(h.HiddenCount).IsEqualTo(1);

        h.Engine.OnTargetProgress(ResetId, 0.45, 300_000);              // engagement 2 (id 2)
        Check.That(h.ShownCount).IsEqualTo(2);

        h.Engine.OnBounds(1, PreviewBoundsKind.NoPanel, null);          // stale from engagement 1
        Check.That(h.Engine.FrozenSilent).IsFalse();                    // dropped: no cancel applied
        Check.That(h.HiddenCount).IsEqualTo(1);
        Check.That(h.ShownCount).IsEqualTo(2);

        // D14: engagement 1's rect does not inflate engagement 2's union during its unknown
        // window — a point inside the OLD rect is excluded (had the rect leaked, it would be
        // FeedNone).
        Check.That(h.Engine.DecideFeed(false, InPanel, 350_000)).IsEqualTo(PreviewFeed.FeedExcluded);
    }

    [Fact]
    public void AfterFirstRect_NoFurtherUnknownWindow_LeaveRunsOnDebounceAlone()
    {
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);
        Check.That(h.Engine.DecideFeed(false, InPanel, 150_000)).IsEqualTo(PreviewFeed.FeedNone);

        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, MovedPanelRect);   // resize: replace, no new window

        // The out-of-union point starts the run IMMEDIATELY (no 500 ms unknown stall), and the
        // debounce alone arms: 130 ms of continuous off-union time.
        Check.That(h.Engine.DecideFeed(false, InPanel, 200_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.DecideFeed(false, InPanel, 330_000)).IsEqualTo(PreviewFeed.FeedExcluded);
        Check.That(h.Engine.IsArmed).IsTrue();
        Check.That(h.HiddenCount).IsEqualTo(1);
    }

    [Fact]
    public void ReturnAtExactly3_000s_ArmExpiryBoundary_FiresUnfrozen()
    {
        // The expiry is STRICTLY past 3 s: the first Reset progress of the return arriving at
        // exactly 3.000 s after arming is still within the window — the pass arms and runs to the
        // fire with no second preview.
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                      // armed at 220_000
        Check.That(h.Engine.IsArmed).IsTrue();

        Check.That(h.Engine.OnTargetProgress(ResetId, 0.20, 220_000 + 3_000_000))
            .IsEqualTo(PreviewProgress.Propagate);                      // exactly 3.0 s: within
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.60, 220_000 + 3_500_000))
            .IsEqualTo(PreviewProgress.Propagate);                      // mid-pass: past 0.40, no freeze
        Check.That(h.Engine.OnTargetProgress(ResetId, 1.00, 220_000 + 4_000_000))
            .IsEqualTo(PreviewProgress.Propagate);
        h.Engine.OnActivated(ResetId);                                  // the fire

        Check.That(h.Engine.IsArmed).IsFalse();
        Check.That(h.ShownCount).IsEqualTo(1);
    }

    // --- M2 (follow-up): in the grown overlap, Reset's own abandon ends an armed pass ---

    [Fact]
    public void ArmedPass_EndsOnResetsOwnAbandon_UnderGrownOverlap_LateRestRepreviews()
    {
        // M2 regression: with gaze resting in the Modes/Reset grown overlap, HitTest(Reset) stays
        // true while Modes is nearest — no off-button DecideFeed ever arrives — but Reset's
        // detector still emits progress 0 (its abandon). Without ending the pass on that abandon
        // the pass outlived it, and a re-dwell more than 3 s after arming fired unfrozen with no
        // fresh preview — violating D13's "runs to fire or abandon".
        var h = new Harness();
        h.Engine.OnTargetProgress(ResetId, 0.40, 100_000);
        h.Engine.OnBounds(1, PreviewBoundsKind.Rect, PanelRect);      // Task 5: known bounds
        h.Engine.DecideFeed(false, Away, 100_000);
        h.Engine.DecideFeed(false, Away, 220_000);                    // armed at 220_000
        Check.That(h.Engine.IsArmed).IsTrue();

        // The pass starts: the gaze return's first Reset progress > 0 within the window. Every
        // DecideFeed of the return sees gazeOverReset=true (the overlap), so no off-button
        // sample can end the pass — only the abandon below can.
        Check.That(h.Engine.DecideFeed(true, OnReset, 1_000_000)).IsEqualTo(PreviewFeed.FeedAll);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.25, 1_050_000)).IsEqualTo(PreviewProgress.Propagate);

        // Reset's own abandon while STILL HitTest-true: Modes took over as nearest and Reset's
        // detector reset. This event must end the pass; its 0 propagates (mid-pass the arc was
        // never held, so it falls naturally).
        Check.That(h.Engine.DecideFeed(true, OnReset, 1_100_000)).IsEqualTo(PreviewFeed.FeedAll);
        Check.That(h.Engine.OnTargetProgress(ResetId, 0.0, 1_150_000)).IsEqualTo(PreviewProgress.Propagate);

        // A rest more than 3 s after ARMING re-PREVIEWS (fresh engagement id) instead of firing
        // unfrozen through the zombie pass.
        var got = h.Engine.OnTargetProgress(ResetId, 0.41, 220_000 + 3_100_000);

        Check.That(got).IsEqualTo(PreviewProgress.HoldAtFreeze);
        Check.That(h.ShownCount).IsEqualTo(2);
        Check.That(h.Visibility[^1].Id).IsEqualTo(2);
        Check.That(h.Engine.IsArmed).IsFalse();
    }
}
