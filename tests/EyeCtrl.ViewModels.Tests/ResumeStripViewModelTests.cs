namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// B2a's Resume target, driven through the real messenger exactly as GazeEngineAdapter delivers gaze
/// (GazeCursorMovedMessage with IsPaused stamped). The acceptance rows: a paused rest for the
/// effective dwell resumes; a blink or a short glance mid-dwell (A1 grace) still completes; a gaze
/// already on Resume when it appears needs a look away and back; the band hides it; and the effective
/// dwell is max(ResumeDwellMs, toolbar + 200).
/// </summary>
public class ResumeStripViewModelTests
{
    private static readonly PhysicalPoint Centre = new(1920, 300);
    private static readonly PhysicalPoint Away = new(1920, 900);
    private const double Radius = 100;
    private const long Step = 16_000;

    private sealed class Rig
    {
        public required ResumeStripViewModel Vm { get; init; }
        public required WeakReferenceMessenger Messenger { get; init; }
        public required EyeCtrlSettings Settings { get; init; }
        public int Toggles;
        public long Now;

        /// <summary>Streams samples at <paramref name="at"/> for <paramref name="durationUs"/>,
        /// one every 16 ms, ending exactly at Now + duration.</summary>
        public void Hold(PhysicalPoint at, long durationUs, bool paused = true, bool gap = false)
        {
            var end = Now + durationUs;
            while (Now < end)
            {
                Now = Math.Min(Now + Step, end);
                Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(at, Now, gap)) { IsPaused = paused });
            }
        }

        /// <summary>One paused sample at exactly <paramref name="timestampUs"/>.</summary>
        public void At(PhysicalPoint at, long timestampUs)
        {
            Now = timestampUs;
            Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(at, Now, IsInGap: false)) { IsPaused = true });
        }
    }

    private static Rig Build(int toolbarDwellMs = 700, int resumeDwellMs = 1000, bool shown = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = toolbarDwellMs;
        settings.Dwell.ResumeDwellMs = resumeDwellMs;
        var messenger = new WeakReferenceMessenger();
        var vm = new ResumeStripViewModel(settings, messenger);
        var rig = new Rig { Vm = vm, Messenger = messenger, Settings = settings, Now = 1_000_000 };
        messenger.Register<PauseToggledMessage>(rig, (r, _) => ((Rig)r).Toggles++);

        vm.PlaceTarget(Centre, Radius);
        vm.SetBandVisible(false);
        if (shown) vm.SetStripShown(true);
        return rig;
    }

    /// <summary>The look away that arms a freshly shown target.</summary>
    private static void Arm(Rig rig)
    {
        rig.Hold(Away, ResumeStripViewModel.ArmLeaveMicroseconds + Step);
        Check.WithCustomMessage("the look away should have armed the target").That(rig.Vm.IsArmed).IsTrue();
    }

    [Fact]
    public void Paused_RestOnResumeForTheEffectiveDwell_SendsTheToolbarPlayToggleOnce()
    {
        var rig = Build();
        Arm(rig);

        // The detector times from the first sample inside; 1000 ms is the effective dwell here
        // (max(1000, 700 + 200)).
        rig.Hold(Centre, 16_000);
        rig.Hold(Centre, 1_000_000 - 2 * Step);
        Check.WithCustomMessage("Resume fired before its 1000 ms dwell").That(rig.Toggles).IsEqualTo(0);

        rig.Hold(Centre, 2 * Step);
        Check.That(rig.Toggles).IsEqualTo(1);

        // One look, one toggle: the target disarms on firing, so a gaze that stays does not re-fire.
        rig.Hold(Centre, 3_000_000);
        Check.That(rig.Toggles).IsEqualTo(1);
    }

    [Fact]
    public void EffectiveDwell_IsTheToolbarDwellPlus200_WhenThatIsLonger()
    {
        var rig = Build(toolbarDwellMs: 900, resumeDwellMs: 1000);

        Check.That(ResumeStripViewModel.EffectiveDwellMs(rig.Settings.Dwell)).IsEqualTo(1100);
        Check.That(rig.Vm.DetectorForTest.Targets.Single().DwellTimeMicroseconds).IsEqualTo(1_100_000);

        Arm(rig);
        rig.Hold(Centre, 16_000);
        rig.Hold(Centre, 1_050_000);
        Check.WithCustomMessage("with the toolbar at 900 ms Resume must take 1100 ms, not 1000")
            .That(rig.Toggles).IsEqualTo(0);
        rig.Hold(Centre, 100_000);
        Check.That(rig.Toggles).IsEqualTo(1);
    }

    [Fact]
    public void EffectiveDwell_IsResumeDwellMs_WhenThatIsLonger_AndFollowsALiveChange()
    {
        var rig = Build(toolbarDwellMs: 700, resumeDwellMs: 1000);
        Check.That(rig.Vm.DetectorForTest.Targets.Single().DwellTimeMicroseconds).IsEqualTo(1_000_000);

        rig.Settings.Dwell.ResumeDwellMs = 1500;
        Check.That(rig.Vm.DetectorForTest.Targets.Single().DwellTimeMicroseconds).IsEqualTo(1_500_000);

        rig.Settings.Dwell.ToolbarButtonDwellMs = 1600;
        Check.That(rig.Vm.DetectorForTest.Targets.Single().DwellTimeMicroseconds).IsEqualTo(1_800_000);
    }

    [Fact]
    public void AGlanceAwayMidDwell_IsForgivenByThePauseGrace_AndStillCompletes()
    {
        var rig = Build();
        Arm(rig);

        rig.Hold(Centre, 500_000);
        // 150 ms away (166 ms until the gaze is back), inside the Resume target's 200 ms grace: the
        // dwell holds its progress and resumes.
        rig.Hold(Away, 150_000);
        Check.That(rig.Vm.IsDwellPaused).IsTrue();
        rig.Hold(Centre, 600_000);

        // Credited 500 + 600 = 1100 ms ≥ 1000. Without the grace the dwell would have restarted on
        // return and need until 1000 ms after it.
        Check.WithCustomMessage("a glance away inside the pause grace must not restart Resume")
            .That(rig.Toggles).IsEqualTo(1);
    }

    [Fact]
    public void ABlinkMidDwell_StillCompletes()
    {
        var rig = Build();
        Arm(rig);

        rig.Hold(Centre, 400_000);
        // The eyes closed for 150 ms (166 ms between real samples): gap-held samples, never fed. A
        // blink of up to the 200 ms Resume grace is forgiven.
        rig.Hold(Centre, 150_000, gap: true);
        rig.Hold(Centre, 700_000);

        Check.That(rig.Toggles).IsEqualTo(1);
    }

    [Fact]
    public void GazeAlreadyOnResumeWhenItAppears_DoesNotFire_UntilALookAwayAndBack()
    {
        var rig = Build(shown: false);
        rig.Hold(Centre, 200_000);   // the gaze rests where the target is about to appear
        rig.Vm.SetStripShown(true);  // the pause begins with the eyes there

        rig.Hold(Centre, 3_000_000);
        Check.WithCustomMessage("a resting gaze resumed on its own").That(rig.Toggles).IsEqualTo(0);
        Check.That(rig.Vm.IsArmed).IsFalse();
        Check.That(rig.Vm.DwellProgress).IsEqualTo(0.0);

        // A glance shorter than the 150 ms leave is not a look away.
        rig.Hold(Away, 100_000);
        rig.Hold(Centre, 2_000_000);
        Check.That(rig.Toggles).IsEqualTo(0);

        // A real look away arms it; looking back then dwells normally.
        Arm(rig);
        rig.Hold(Centre, 1_100_000);
        Check.That(rig.Toggles).IsEqualTo(1);
    }

    [Fact]
    public void ABlinkDoesNotCountAsTheLookAway()
    {
        var rig = Build();
        rig.Hold(Away, 100_000);
        rig.Hold(Away, 300_000, gap: true);
        rig.Hold(Away, 100_000);
        Check.WithCustomMessage("a gap restarts the leave clock: 100 + 100 ms is not 150 ms away")
            .That(rig.Vm.IsArmed).IsFalse();
    }

    [Fact]
    public void TheBandShowing_HidesTheTarget_AndItsReturn_ShowsItDisarmed()
    {
        var rig = Build();
        Arm(rig);
        Check.That(rig.Vm.IsVisible).IsTrue();

        rig.Vm.SetBandVisible(true);
        Check.That(rig.Vm.IsVisible).IsFalse();
        rig.Hold(Centre, 3_000_000);
        Check.WithCustomMessage("Resume fired while the band (and its Play) was showing")
            .That(rig.Toggles).IsEqualTo(0);

        rig.Vm.SetBandVisible(false);
        Check.That(rig.Vm.IsVisible).IsTrue();
        Check.That(rig.Vm.IsArmed).IsFalse();
        rig.Hold(Centre, 3_000_000);
        Check.WithCustomMessage("the band slid away from under the gaze: no fire without a look away")
            .That(rig.Toggles).IsEqualTo(0);
    }

    [Fact]
    public void NeverShownOutsidePaused()
    {
        var rig = Build(shown: false);
        Check.That(rig.Vm.IsVisible).IsFalse();

        rig.Vm.SetStripShown(true);
        Check.That(rig.Vm.IsVisible).IsTrue();

        rig.Vm.SetStripShown(false);
        Check.That(rig.Vm.IsVisible).IsFalse();
        rig.Hold(Away, 300_000);
        rig.Hold(Centre, 3_000_000);
        Check.That(rig.Toggles).IsEqualTo(0);
    }

    [Fact]
    public void AnActivationOnAnUnpausedSample_DoesNotToggle()
    {
        // PauseToggledMessage TOGGLES: sent from FreeLook it would pause again.
        var rig = Build();
        rig.Hold(Away, 300_000, paused: false);
        rig.Hold(Centre, 1_500_000, paused: false);
        Check.That(rig.Toggles).IsEqualTo(0);
    }

    // ── B2a fixup-01 F1 (owner: "shorter grace on Resume") ──

    // The owner's geometry at 3840×2400 @ 200 %: the disc at (1920, 404) px, r = 100 px; each look
    // away lands 60 px past the disc's edge (review 01, probe P8).
    private static readonly PhysicalPoint OwnerDisc = new(1920, 404);
    private static readonly PhysicalPoint OwnerLookAway = new(1920, 404 + 100 + 60);

    /// <summary>
    /// Review 01 F1: with A1's full 500 ms grace, short visits while reading near the disc added up to
    /// a resume (400/300 ms in 1.8 s, 250/250 ms in 2.75 s). With the Resume grace capped at 200 ms,
    /// every look away of 250 ms or more drops the dwell, so reading stays Paused. Mutation: the full
    /// grace again → resumes within 3 s → red.
    /// </summary>
    [Theory]
    [InlineData(400_000, 300_000)]
    [InlineData(250_000, 250_000)]
    public void ShortVisitsWhileReading_DoNotAddUpToAResume_For20Seconds(long onUs, long offUs)
    {
        var rig = Build();
        rig.Vm.PlaceTarget(OwnerDisc, Radius);
        rig.Hold(OwnerLookAway, ResumeStripViewModel.ArmLeaveMicroseconds + Step);
        Check.That(rig.Vm.IsArmed).IsTrue();

        var end = rig.Now + 20_000_000;
        while (rig.Now < end)
        {
            rig.Hold(OwnerDisc, onUs);
            rig.Hold(OwnerLookAway, offUs);
        }

        Check.WithCustomMessage($"visits of {onUs / 1000} ms on / {offUs / 1000} ms away resumed the pause")
            .That(rig.Toggles).IsEqualTo(0);
    }

    [Fact]
    public void ThePauseGrace_IsCappedAt200_AndFollowsTheSetting()
    {
        var rig = Build();
        Check.WithCustomMessage("the default 500 ms grace is capped to 200 on Resume")
            .That(rig.Vm.DetectorForTest.PauseGraceMs).IsEqualTo(ResumeStripViewModel.MaxPauseGraceMs);
        Check.That(ResumeStripViewModel.MaxPauseGraceMs).IsEqualTo(200);

        rig.Settings.Dwell.DwellPauseGraceMs = 100;
        Check.That(rig.Vm.DetectorForTest.PauseGraceMs).IsEqualTo(100);

        rig.Settings.Dwell.DwellPauseGraceMs = 0;
        Check.WithCustomMessage("a grace of 0 (reset-on-leave) stays 0")
            .That(rig.Vm.DetectorForTest.PauseGraceMs).IsEqualTo(0);

        rig.Settings.Dwell.DwellPauseGraceMs = 1000;
        Check.That(rig.Vm.DetectorForTest.PauseGraceMs).IsEqualTo(200);
    }

    [Fact]
    public void AZeroGraceSetting_StartsAtZero()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.DwellPauseGraceMs = 0;
        var vm = new ResumeStripViewModel(settings, new WeakReferenceMessenger());
        Check.That(vm.DetectorForTest.PauseGraceMs).IsEqualTo(0);
    }

    [Fact]
    public void TheToolbarBandAndTheEdgeBar_KeepTheFullGrace()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var resume = new ResumeStripViewModel(settings, messenger);
        var toolbar = new ToolbarViewModel(settings, messenger);
        var edgeBar = new EdgeButtonBarViewModel(settings, messenger);

        Check.That(settings.Dwell.DwellPauseGraceMs).IsEqualTo(500);
        Check.That(toolbar.DetectorForTest.PauseGraceMs).IsEqualTo(500);
        Check.That(edgeBar.DetectorForTest.PauseGraceMs).IsEqualTo(500);
        Check.That(resume.DetectorForTest.PauseGraceMs).IsEqualTo(200);

        settings.Dwell.DwellPauseGraceMs = 300;
        Check.That(toolbar.DetectorForTest.PauseGraceMs).IsEqualTo(300);
        Check.That(resume.DetectorForTest.PauseGraceMs).IsEqualTo(200);
    }

    // ── B2a fixup-01 F2/F3: the leave-before-arm, pinned ──

    /// <summary>
    /// F2: a target that moves while visible starts disarmed again — a gaze already resting where the
    /// moved disc lands (a display or DPI change, a band-rect refresh) must look away first.
    /// Mutation: drop the Disarm in PlaceTarget → fires → red.
    /// </summary>
    [Fact]
    public void AMovedTarget_UnderARestingGaze_DoesNotFire_WithoutALookAway()
    {
        var rig = Build();
        Arm(rig);
        var moved = new PhysicalPoint(Centre.X, Centre.Y + 300);
        rig.Hold(moved, 100_000); // the gaze already rests where the disc is about to land

        rig.Vm.PlaceTarget(moved, Radius);
        Check.That(rig.Vm.IsVisible).IsTrue();
        rig.Hold(moved, 3_000_000);

        Check.WithCustomMessage("a moved disc fired under a gaze that never looked away")
            .That(rig.Toggles).IsEqualTo(0);
        Check.That(rig.Vm.IsArmed).IsFalse();
    }

    /// <summary>
    /// F3, length: the look away arms only after 150 ms continuously outside — 140 ms is not enough,
    /// 150 ms is. Samples at exact times, so the boundary is the threshold itself. Mutation: 100 ms
    /// (arms at 140) or 160 ms (does not arm at 150) → red.
    /// </summary>
    [Fact]
    public void TheLookAway_ArmsAt150Ms_NotAt140()
    {
        var rig = Build();
        var t = rig.Now + 1_000_000;
        rig.At(Centre, t);
        rig.At(Away, t + 10_000);              // the outside clock starts here
        rig.At(Away, t + 10_000 + 140_000);
        Check.WithCustomMessage("armed after only 140 ms away").That(rig.Vm.IsArmed).IsFalse();

        rig.At(Away, t + 10_000 + 150_000);
        Check.WithCustomMessage("not armed after 150 ms away").That(rig.Vm.IsArmed).IsTrue();
    }

    /// <summary>
    /// F3, continuity: 100 ms away, back on the disc, 100 ms away again is not a 150 ms look away —
    /// coming back restarts the clock. Mutation: keep the clock running across the return → armed → red.
    /// </summary>
    [Fact]
    public void TwoShortLookAways_DoNotAddUp()
    {
        var rig = Build();
        var t = rig.Now + 1_000_000;
        rig.At(Away, t);
        rig.At(Away, t + 100_000);
        rig.At(Centre, t + 110_000);           // back on the disc
        rig.At(Away, t + 120_000);
        rig.At(Away, t + 220_000);

        Check.WithCustomMessage("two 100 ms look aways added up to an arm")
            .That(rig.Vm.IsArmed).IsFalse();
    }

    [Fact]
    public void BeforePlacement_TheTargetIsNeverVisible()
    {
        var settings = new EyeCtrlSettings();
        var vm = new ResumeStripViewModel(settings, new WeakReferenceMessenger());
        vm.SetBandVisible(false);
        vm.SetStripShown(true);

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.DetectorForTest.Targets).IsEmpty();
    }
}
