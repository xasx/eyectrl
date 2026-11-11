namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// "One look, one key" (keyboard-UX round 01, item 1 A): after a key fires, that same key cannot
/// start another dwell until the gaze has been continuously off it for
/// <see cref="KeyboardViewModel.RearmLeaveUs"/>. Before this, only the post-activation cooldown
/// stood between a resting gaze and the next fire: the owner's log shows Enter firing three times
/// in 2.5 s, the second 1028 ms after the first with <c>restarts=0</c> and a 483 ms hold-on. A
/// sample on another key, a null pick and an outside-the-window gaze all count as a leave; a
/// tracker pause longer than the gap clamp neither counts toward one nor completes one.
///
/// <para>
/// Item 1 B, the second half of the file: a SHOW arms the same one-shot landing guard the
/// un-yield uses, but only for a gaze already where the keyboard appears — the first key-area
/// sample must be the first sample after the show or arrive within
/// <see cref="KeyboardViewModel.ShowLandingReactionWindowUs"/>. Two of eight ring-opened shows
/// typed a letter 432–453 ms after the keyboard appeared, because a gaze already resting where a
/// key materializes simply started its dwell. A show anchor is released by a look off the key
/// area as well as by the half-key travel, so a deliberate look away and back types.
/// </para>
/// </summary>
public class KeyboardOneLookOneKeyTests
{
    private const int DwellMs = 450;
    private const int CooldownMs = 400;
    private const long StepUs = 30_000;

    private static readonly PhysicalPoint Enter = new(100, 100);
    private static readonly PhysicalPoint Letter = new(300, 100);
    private static readonly PhysicalPoint Clear = new(1000, 1000);

    // Pitch 200 → the landing guard's half-key radius is 100. Enter and the letter sit in
    // adjacent 200-wide boxes, so the box-first pick and the nearest-centre fallback agree.
    private static readonly Dictionary<string, PhysicalPoint> TwoKeys = new()
    {
        ["enter"] = Enter,
        ["key_0_0"] = Letter,
    };

    private static readonly Dictionary<string, PhysicalRect> Boxes = new()
    {
        ["enter"] = new PhysicalRect(0, 0, 200, 200),
        ["key_0_0"] = new PhysicalRect(200, 0, 200, 200),
    };

    private static (KeyboardViewModel Vm, List<string> Fired) Rig()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Configure(DwellMs, CooldownMs);
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;
        return (vm, fired);
    }

    /// <summary>One sample every 30 ms from <paramref name="fromUs"/> through the bound.</summary>
    private static void Feed(KeyboardViewModel vm, PhysicalPoint at, long fromUs, long throughUs,
        bool inside = true)
    {
        for (var t = fromUs; t <= throughUs; t += StepUs)
            vm.ProcessKeyGaze(TwoKeys, at, inside, t, Boxes);
    }

    /// <summary>Holds Enter long enough to fire it (the 600 ms app-escaping floor) and rest on.</summary>
    private static (KeyboardViewModel Vm, List<string> Fired) FiredEnter()
    {
        var (vm, fired) = Rig();
        Feed(vm, Enter, 0, 1_000_000);
        Check.WithCustomMessage("arrange: the first Enter must fire").That(fired).ContainsExactly("enter");
        return (vm, fired);
    }

    // ---- 1 A: leave-before-re-arm on every key ------------------------------------------------

    [Fact]
    public void RestingOnEnterForThreeSeconds_FiresExactlyOnce()
    {
        // Enter holds the 600 ms app-escaping floor; its cooldown ends at 1 s. Without the latch
        // the gaze keeps re-arming it for the next 2 s (the measured defect: three fires in 2.5 s).
        var (vm, fired) = Rig();
        Feed(vm, Enter, 0, 3_000_000);

        Check.That(fired).ContainsExactly("enter");
    }

    [Fact]
    public void RestingOnALetterForThreeSeconds_FiresExactlyOnce()
    {
        // Letters are included: "0 resting repeats measured" is not a licence to leave them out.
        var (vm, fired) = Rig();
        Feed(vm, Letter, 0, 3_000_000);

        Check.That(fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void AOneSampleExcursionToANeighbour_DoesNotRearmTheKey()
    {
        var (vm, fired) = FiredEnter();

        // A single stray sample on the neighbour and back: not a look away, so no re-arm.
        vm.ProcessKeyGaze(TwoKeys, Letter, true, 1_030_000, Boxes);
        Feed(vm, Enter, 1_060_000, 3_000_000);

        Check.That(fired).ContainsExactly("enter");
    }

    [Theory]
    [InlineData(90, 1)]     // below the 120 ms leave: still one look
    [InlineData(120, 2)]    // exactly at the boundary: re-armed (the comparison is `>=`)
    [InlineData(150, 2)]    // comfortably past it
    public void AnExcursionOfAtLeastTheLeaveTime_RearmsTheKey(int offMs, int expectedFires)
    {
        var (vm, fired) = FiredEnter();

        // Look at the neighbour for offMs, then back. 120 is the same rule the prediction bar's
        // slot latch uses; 90 must not be enough.
        Feed(vm, Letter, 1_030_000, 1_030_000 + offMs * 1_000L);
        Feed(vm, Enter, 1_060_000 + offMs * 1_000L, 3_000_000);

        Check.That(fired).HasSize(expectedFires);
    }

    [Fact]
    public void AOneSecondDropoutWhileResting_DoesNotRearmTheKey()
    {
        var (vm, fired) = FiredEnter();

        // The stream is silent for a second while the gaze rests on the fired key (a blink, a
        // tracker dropout): a pause is not a look away.
        vm.ProcessKeyGaze(TwoKeys, Enter, true, 2_000_000, Boxes);
        Feed(vm, Enter, 2_030_000, 4_000_000);

        Check.That(fired).ContainsExactly("enter");
    }

    [Fact]
    public void ADropoutDuringTheLeave_DoesNotCompleteIt()
    {
        var (vm, fired) = FiredEnter();

        // The gaze moved off (the leave clock starts), then the stream drops out for a second on
        // the neighbour. Crediting the silence would complete the leave on one sample; the rule
        // is that a gap neither counts toward a leave nor completes one.
        vm.ProcessKeyGaze(TwoKeys, Letter, true, 1_030_000, Boxes);
        vm.ProcessKeyGaze(TwoKeys, Letter, true, 2_030_000, Boxes);

        // Back on the fired key: still one look, so it may not fire again.
        Feed(vm, Enter, 2_060_000, 3_500_000);

        Check.That(fired).ContainsExactly("enter");
    }

    [Fact]
    public void NonContiguousOffSamples_DoNotRearmTheKey()
    {
        // F2: a leave is 120 ms CONTIGUOUS. Short off runs with a sample back on the fired key
        // between them never add up: three off samples (60 ms) then one on-key sample, repeated.
        var (vm, fired) = FiredEnter();

        var t = 1_030_000;
        for (var i = 0; i < 8; i++)
        {
            Feed(vm, Letter, t, t + 60_000);
            vm.ProcessKeyGaze(TwoKeys, Enter, true, t + 90_000, Boxes);
            t += 120_000;
        }

        // Now rest on the fired key: if the short runs had added up, the key would fire here.
        Feed(vm, Enter, t, t + 1_000_000);
        Check.WithCustomMessage("non-contiguous off runs added up to a leave, so a resting gaze re-armed")
            .That(fired).ContainsExactly("enter");

        // A contiguous leave of at least 120 ms does re-arm it.
        t += 1_030_000;
        Feed(vm, Letter, t, t + 150_000);
        Feed(vm, Enter, t + 180_000, t + 1_500_000);

        Check.That(fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void ALeaveDuringTheCooldown_StillRearmsTheKey()
    {
        // F3: the leave clock must see the samples during the post-activation cooldown, which
        // return before any dwell decision. Fire at 600 ms, cooldown ends at 1 s.
        var (vm, fired) = Rig();
        Feed(vm, Enter, 0, 600_000);
        Check.That(fired).ContainsExactly("enter");

        Feed(vm, Letter, 630_000, 780_000);       // a 150 ms leave, wholly inside the cooldown
        Feed(vm, Enter, 810_000, 2_500_000);      // back; the cooldown ends at 1 s

        // After the cooldown the re-armed Enter dwells and fires again.
        Check.That(fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void OnlyTheFiredKeyIsLatched_OtherTargetsAndNoKeyAreNot()
    {
        var (vm, _) = FiredEnter();

        Check.That(vm.RearmNeedsLeave("enter")).IsTrue();
        Check.That(vm.RearmNeedsLeave("key_0_0")).IsFalse();
        Check.That(vm.RearmNeedsLeave(null)).IsFalse();
    }

    [Fact]
    public void TheLatchIsClearedByHide_SoTheNextSessionCanTypeAtOnce()
    {
        var (vm, fired) = FiredEnter();

        vm.IsVisible = true;
        vm.IsVisible = false;    // the hide reset clears every interaction state
        vm.IsVisible = true;

        Check.That(vm.RearmNeedsLeave("enter")).IsFalse();

        // The reopened session's first look dwells normally (a hide is not a leave; the reset is
        // what makes reopening the recovery path).
        Feed(vm, Enter, 1_100_000, 2_500_000);

        Check.That(fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void TheLatchIsClearedByALayoutReplacement()
    {
        var (vm, fired) = FiredEnter();
        Check.That(vm.RearmNeedsLeave("enter")).IsTrue();

        vm.SetLayout(Layout());
        Check.WithCustomMessage("a layout replacement kept a latch measured on the old grid")
            .That(vm.RearmNeedsLeave("enter")).IsFalse();

        Feed(vm, Enter, 1_100_000, 2_500_000);
        Check.That(fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void TheLatchIsClearedByASubLayoutSwap()
    {
        // A swap rebuilds the grid, so an id may name a different key under the same name ("enter"
        // exists on several pages). The swap's own D13 anchor guards the landing point.
        var (vm, fired) = Rig();
        vm.SetLayout(Layout());
        Feed(vm, Enter, 0, 1_000_000);
        Check.That(fired).ContainsExactly("enter");
        Check.That(vm.RearmNeedsLeave("enter")).IsTrue();

        vm.SwitchSubLayout("numbers");

        Check.That(vm.RearmNeedsLeave("enter")).IsFalse();
    }

    [Fact]
    public void TheLatchIsClearedByAnEmojiModeChange()
    {
        // The emoji surface replaces the key grid exactly like a sub-layout swap.
        var (vm, _) = FiredEnter();
        Check.That(vm.RearmNeedsLeave("enter")).IsTrue();

        vm.IsEmojiMode = true;

        Check.That(vm.RearmNeedsLeave("enter")).IsFalse();
    }

    // ---- 1 B: the landing guard at show -------------------------------------------------------

    [Fact]
    public void GazeRestingWhereAKeyAppearsAtShow_DoesNotTypeUntilItMovesPastTheRadius()
    {
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();

        // The first sample is already on Enter: this is the landing, not a dwell.
        Feed(vm, Enter, 0, 3_000_000);
        Check.WithCustomMessage("a key fired under a gaze already resting where the keyboard appeared")
            .That(fired).IsEmpty();

        // 120 units is past the 100-unit half-key radius: the anchor clears and typing resumes.
        Feed(vm, new PhysicalPoint(220, 100), 3_030_000, 4_500_000);

        Check.That(fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void ShowLanding_TheFirstInKeySampleWithinTheWindow_IsStillGuarded()
    {
        // The show has no gaze timestamp, so the window starts at the first sample AFTER it. That
        // sample is off the keys here; the first key-area sample 30 ms later is the landing.
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 0, Boxes);

        Feed(vm, Enter, 30_000, 2_000_000);

        Check.That(fired).IsEmpty();
    }

    [Theory]
    [InlineData(150_000, false)]  // exactly at the 150 ms reaction boundary: still the landing (`<=`)
    [InlineData(180_000, true)]   // after it: a visually guided look, so it types
    public void ShowLanding_TheReactionWindowExpiresAfterTheFirstSample(long firstKeySampleUs, bool types)
    {
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 0, Boxes);

        Feed(vm, Enter, firstKeySampleUs, firstKeySampleUs + 1_500_000);

        if (types)
            Check.WithCustomMessage("a first key-area sample after the reaction window was still suppressed")
                .That(fired).ContainsExactly("enter");
        else
            Check.WithCustomMessage("the first key-area sample exactly at the reaction boundary fired")
                .That(fired).IsEmpty();
    }

    [Fact]
    public void ADeliberateFirstLookAtAKeyAfterTheShow_Types()
    {
        // F1: the show guard is for a gaze ALREADY on the keys. A look that travels to a key
        // 500 ms after the show is a deliberate reaction to the keyboard appearing (the owner log
        // has a real first key at 851 ms) and must dwell normally, not become a dead key.
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 0, Boxes);   // first sample after the show: elsewhere
        Feed(vm, Letter, 500_000, 1_500_000);                 // deliberately looks at a key

        Check.That(fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void RestAtShow_ThenLookAwayFromTheKeyAreaAndBack_Types()
    {
        // F1: a show anchor is released by RearmLeaveUs of continuous off-key-area gaze, the same
        // rule as the leave latch. Before the fixup it held until an in-grid move, so looking away
        // and back did not recover the key — for this owner a stuck key is worse than a repeat.
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        Feed(vm, Enter, 0, 800_000);                          // rest at show: anchored, no fire
        Check.That(fired).IsEmpty();

        Feed(vm, Clear, 830_000, 980_000, inside: false);     // 150 ms off the key area
        Feed(vm, Enter, 1_010_000, 2_500_000);                // back: a deliberate look

        Check.That(fired).ContainsExactly("enter");
    }

    // ---- the show release must not leak into D13 (review 02 G1) --------------------------------

    [Fact]
    public void AnUnYieldAnchor_IsNotReleasedByAnOffKeyLook()
    {
        // The un-yield keeps its 1000 ms window and its in-grid-only release. An off-key look —
        // long enough to release a SHOW anchor — must not release this one; D13 is unchanged
        // (fixup 01 requirement 3). This is the reviewer's P5 leak probe.
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterUnYield(0);
        Feed(vm, Clear, 0, 270_000, inside: false);
        Feed(vm, Enter, 300_000, 1_200_000);                    // landing inside the 1 s window: anchored
        Feed(vm, Clear, 1_230_000, 1_800_000, inside: false);   // 570 ms off the key area
        Feed(vm, Enter, 1_830_000, 3_500_000);                  // back on the anchor

        Check.WithCustomMessage("an un-yield anchor was released by an off-key look")
            .That(fired).IsEmpty();
    }

    [Fact]
    public void ASwapAnchor_IsNotReleasedByAnOffKeyLook()
    {
        // Same leak check for a sub-layout swap's D13 anchor (reviewer P6).
        var (vm, fired) = Rig();
        vm.SetLayout(Layout());
        Feed(vm, Letter, 0, 300_000);                           // gaze on the grid (last in-grid point)
        vm.SwitchSubLayout("numbers");                          // D13 anchor at the letter
        Feed(vm, Clear, 330_000, 900_000, inside: false);       // 570 ms off the key area
        Feed(vm, Letter, 930_000, 3_000_000);                   // back on the anchor

        Check.WithCustomMessage("a swap anchor was released by an off-key look")
            .That(fired).IsEmpty();
    }

    [Fact]
    public void ASwapAfterAShowAnchor_IsStillAPlainD13Anchor()
    {
        // A stale "this anchor is a show's" flag must not survive a swap: after a show anchor is
        // released in-grid, the next swap's anchor must keep the plain in-grid-only release
        // (reviewer P7).
        var (vm, fired) = Rig();
        vm.SetLayout(Layout());
        vm.ArmLandingSuppressionAfterShow();
        Feed(vm, Enter, 0, 600_000);                            // show anchor at Enter
        Feed(vm, Letter, 630_000, 750_000);                     // past the radius: released in-grid
        vm.SwitchSubLayout("numbers");                          // fresh swap anchor at the letter
        Feed(vm, Clear, 780_000, 1_400_000, inside: false);     // 620 ms off the key area
        Feed(vm, Letter, 1_430_000, 3_000_000);

        Check.WithCustomMessage("a swap after a show anchor inherited the show's off-key release")
            .That(fired).IsEmpty();
    }

    // ---- the edges of the show release (review 02 G2) ------------------------------------------

    [Fact]
    public void AShowAnchor_SurvivesOneStrayOffKeySample()
    {
        // One noisy sample off the keys is not a look away: the show anchor (and the protection
        // the ticket exists for) must survive it (reviewer P8).
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        Feed(vm, Enter, 0, 600_000);
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 630_000, Boxes);
        Feed(vm, Enter, 660_000, 2_500_000);

        Check.WithCustomMessage("one stray off-key sample released the show anchor")
            .That(fired).IsEmpty();
    }

    [Fact]
    public void AShowAnchor_SurvivesADropoutDuringTheOffKeyLook()
    {
        // A dropout is not evidence the gaze moved: it neither counts toward nor completes the
        // 120 ms release run. Without the gap reset, the second off sample would complete it and
        // the returned gaze would type (reviewer P9).
        var (vm, fired) = Rig();
        vm.ArmLandingSuppressionAfterShow();
        Feed(vm, Enter, 0, 600_000);
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 630_000, Boxes);
        vm.ProcessKeyGaze(TwoKeys, Clear, false, 1_630_000, Boxes);   // 1 s silence, then one off sample
        Feed(vm, Enter, 1_660_000, 3_500_000);

        Check.WithCustomMessage("a dropout during the off-key look released the show anchor")
            .That(fired).IsEmpty();
    }

    private static KeyboardLayout Layout() => new()
    {
        Name = "Test",
        DefaultSubLayout = "letters",
        SubLayouts = new Dictionary<string, SubLayout>
        {
            ["letters"] = new() { Label = "ABC", Rows = new List<List<KeyDefinition>>() },
            ["numbers"] = new() { Label = "123", Rows = new List<List<KeyDefinition>>() },
        },
    };
}
