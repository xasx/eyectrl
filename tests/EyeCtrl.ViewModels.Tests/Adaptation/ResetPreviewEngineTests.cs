namespace EyeCtrl.ViewModels.Tests.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Adaptation;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// ResetPreviewEngine contract tests (seen-latch freeze, Task 4). Everything is driven by explicit
/// sample timestamps in microseconds (P4) — the engine has no clock of its own, so no test sleeps
/// or reads the wall clock. States under test: Disarmed → Frozen → Leaving → Armed, with
/// Threshold = 0.40, LeaveDebounceUs = 120 ms, ArmExpiryUs = 3 s. Task 5's OnBounds is a no-op
/// stub here, so the union is always the Reset hit area (gazeOverReset).
/// </summary>
public class ResetPreviewEngineTests
{
    private const string ResetId = ResetPreviewEngine.ResetId;

    // The engine trusts `gazeOverReset` for the hit area; OnReset/Away only need to be distinct,
    // realistic physical points (the panel-rect branch of the union is dead until Task 5).
    private static readonly PhysicalPoint Anchor = new(960, 540);
    private static readonly PhysicalPoint OnReset = new(960, 540);
    private static readonly PhysicalPoint Away = new(960, 300);

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
            // Task 5's API (no-op stub here): a PreviewVisibility subscriber may synchronously
            // report bounds from inside the event — the engine must already be in its committed
            // state when this runs.
            engine.OnBounds(id, new PhysicalRect(900, 500, 120, 80));
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
        const long DwellUs = 1_000_000;

        long t = 0;
        for (var i = 1; i <= 8; i++)                                // 0.40 at 400 ms → engage
            h.Engine.OnTargetProgress(ResetId, i * 0.05, t += 50_000);
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
}
