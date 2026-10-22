namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// The scroll joystick's VM. Its G7 tests write the process-global <see cref="DpiHelper"/>, so the
/// class joins the project's exclusive DpiHelper collection (DpiHelperWriterIsolationTests' rule)
/// and every writer restores 1.0 in <c>finally</c>.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ScrollOverlayViewModelTests
{
    private static ScrollOverlayViewModel NewVm() =>
        new(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());

    private static readonly PhysicalPoint Anchor = new(1000, 800);

    private static readonly PhysicalPoint StopCenter = new(2000, 600);

    /// <summary>Sample spacing of the helpers below: ~30 Hz, as the engine delivers.</summary>
    private const long StepUs = 30_000;

    /// <summary>
    /// Feeds ordinary samples at <paramref name="at"/> every 30 ms from <paramref name="startUs"/>
    /// for <paramref name="durationMs"/> of sample time (inclusive: 150 ms gives samples at 0…150) and
    /// returns the timestamp of the NEXT sample, so calls chain.
    /// </summary>
    private static long Feed(
        ScrollOverlayViewModel vm, PhysicalPoint at, long startUs, int durationMs)
    {
        long t = startUs;
        for (; t <= startUs + durationMs * 1000L; t += StepUs)
            vm.Receive(new GazeCursorMovedMessage(at, t));
        return t;
    }

    /// <summary>The arming fixation: <see cref="ScrollOverlayViewModel.StartHoldMs"/> of samples.</summary>
    private static long Arm(ScrollOverlayViewModel vm, PhysicalPoint at, long startUs = 0) =>
        Feed(vm, at, startUs, ScrollOverlayViewModel.StartHoldMs);

    [Fact]
    public void GazeAtAnchor_DotIsCentered_NoScrollVelocity()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.Receive(new GazeCursorMovedMessage(Anchor, 0));
        Check.That(vm.AnchorDotOffsetX).IsCloseTo(0, 1e-3);
        Check.That(vm.AnchorDotOffsetY).IsCloseTo(0, 1e-3);
        Check.That(vm.ScrollVelocityX).IsCloseTo(0, 1e-6);
        Check.That(vm.ScrollVelocityY).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void TargetVelocityChanged_FiresWithComputedVelocity()
    {
        var vm = NewVm();
        double cvx = double.NaN, cvy = double.NaN;
        vm.TargetVelocityChanged += (x, y) => { cvx = x; cvy = y; };
        vm.EnterScrollMode(Anchor, StopCenter);
        long t = Arm(vm, Anchor);   // the arming fix: a fixation in the start circle arms
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 600), t));
        Check.That(cvy).IsStrictlyGreaterThan(0);
        Check.That(cvx).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void EnterScrollMode_SetsAnchor()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        Check.That(vm.Anchor).IsEqualTo(Anchor);
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExitScrollMode_ClearsState()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.ExitScrollMode();
        Check.That(vm.IsVisible).IsFalse();
    }

    // ── G6 (scroll start fix, F-S4): velocity only after the first visit to the dead zone ──

    /// <summary>The menu's Scroll slot: screen centre plus the ring radius, straight down (§S4).</summary>
    private static readonly PhysicalPoint ScrollSlot = new(1920, 1660);

    private static readonly PhysicalPoint SlotAnchor = new(2100, 800);

    [Fact]
    public void EntryAtTheScrollSlot_PublishesNoVelocity_UntilTheDeadZoneIsVisited()
    {
        // S4: at entry the eye is on the menu's Scroll slot, ~900 px from the anchor, and the old VM
        // turned that first sample into a capped velocity — the lurch. Mutants: _armed starts true;
        // the arming check removed; arming on dist <= 0 (the +20 px sample below then never arms).
        var vm = NewVm();
        var publishes = new List<(double vx, double vy)>();
        vm.TargetVelocityChanged += (x, y) => publishes.Add((x, y));
        vm.EnterScrollMode(SlotAnchor, new PhysicalPoint(3640, 1200));

        vm.Receive(new GazeCursorMovedMessage(ScrollSlot, 0));   // still on the menu's Scroll slot

        Check.That(vm.IsArmed).IsFalse();
        Check.That(publishes[^1]).IsEqualTo((0.0, 0.0));

        // Inside the start circle but NOT on the anchor: a fixation, not an exact hit.
        long t = Arm(vm, new PhysicalPoint(SlotAnchor.X + 20, SlotAnchor.Y), 30_000);

        Check.That(vm.IsArmed).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(ScrollSlot, t));   // armed: the slot drives now

        Check.WithCustomMessage("the armed slot gaze must scroll down (the slot is below the anchor)")
            .That(publishes[^1].vy).IsStrictlyLessThan(0);
    }

    [Fact]
    public void ArmedAtTheAnchor_ThenAway_MovesImmediately()
    {
        var vm = NewVm();
        double vy = double.NaN;
        vm.TargetVelocityChanged += (_, y) => vy = y;
        vm.EnterScrollMode(SlotAnchor, new PhysicalPoint(3640, 1200));

        long t = Arm(vm, SlotAnchor);                                // the fixation arms
        Check.That(vm.IsArmed).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(2100, 1200), t));

        Check.That(vy).IsStrictlyLessThan(0);                        // follows immediately, as today
    }

    // ── Scroll arming fix: the start circle, the fixation, the rest point ──────

    private static ScrollOverlayViewModel NewVm80() => NewVm80(new WeakReferenceMessenger());

    private static ScrollOverlayViewModel NewVm80(IMessenger messenger, Action<string>? log = null)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 80;
        return new ScrollOverlayViewModel(messenger, settings.Scroll, settings.Dwell, log);
    }

    [Fact]
    public void G1_ArmingNeedsAFixation_NotASingleSample()
    {
        // Mutant M1: arm on the first sample inside the circle → the lone 100-px sample arms, red.
        // The boundary (149 ms no, 150 ms yes) pins the >= against a > mutant too.
        var vm = NewVm80();
        vm.EnterScrollMode(new PhysicalPoint(1000, 1000), new PhysicalPoint(3000, 600));
        var at100 = new PhysicalPoint(1100, 1000);

        vm.Receive(new GazeCursorMovedMessage(at100, 0));
        Check.WithCustomMessage("ONE sample inside the circle must not arm").That(vm.IsArmed).IsFalse();

        Feed(vm, at100, 30_000, 90);   // samples to 120 ms
        Check.WithCustomMessage("120 ms of fixation is short of StartHoldMs").That(vm.IsArmed).IsFalse();

        vm.Receive(new GazeCursorMovedMessage(at100, 149_000));
        Check.WithCustomMessage("149 ms is one ms short").That(vm.IsArmed).IsFalse();

        vm.Receive(new GazeCursorMovedMessage(at100, 150_000));
        Check.WithCustomMessage("a 150-ms run arms").That(vm.IsArmed).IsTrue();
    }

    [Theory]
    [InlineData(2.0, 440.0, 460.0)]
    [InlineData(1.0, 220.0, 230.0)]
    public void G2_TheStartRadius_IsTheScaledStartRadiusDip(double scale, double inside, double outside)
    {
        // The owner runs 2.0: 225 DIP = 450 px (fixup 03: 450 DIP across), and the pair hugs the
        // edge: 440 arms, 460 never does. Mutants: M2 the radius back to the dead zone (80 px) → the
        // inside runs stay unarmed; M3 StartRadiusDip unscaled → at 2.0 the 440-px run stays unarmed
        // (a 225-px circle); fixup-03 M-a the old 175 DIP (350 px) → 440 stays unarmed; M-b 250 DIP
        // (500 px) → 460 arms.
        DpiHelper.Initialize(scale, scale);
        try
        {
            var anchor = new PhysicalPoint(1000, 1000);

            var hit = NewVm80();
            hit.EnterScrollMode(anchor, new PhysicalPoint(3000, 600));
            Arm(hit, new PhysicalPoint(anchor.X + inside, anchor.Y));
            Check.WithCustomMessage($"scale {scale}: 150 ms at {inside} px must arm").That(hit.IsArmed).IsTrue();

            var miss = NewVm80();
            miss.EnterScrollMode(anchor, new PhysicalPoint(3000, 600));
            Arm(miss, new PhysicalPoint(anchor.X + outside, anchor.Y));
            Check.WithCustomMessage($"scale {scale}: 150 ms at {outside} px must NOT arm").That(miss.IsArmed).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void G3_TheRunMustBeUnbroken_AnOutsideSampleResetsIt()
    {
        // Mutant M4: the out-of-circle sample does not reset the run → 100 ms + 100 ms spans 230 ms
        // and arms, red. The restart is proven too: the run that follows arms on its own 150 ms.
        var vm = NewVm80();
        vm.EnterScrollMode(new PhysicalPoint(1000, 1000), new PhysicalPoint(3000, 600));
        var inside = new PhysicalPoint(1100, 1000);

        long t = Feed(vm, inside, 0, 90);                          // 0…90
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1600, 1000), t));   // outside
        t += StepUs;
        t = Feed(vm, inside, t, 90);                               // a fresh 90-ms run

        Check.WithCustomMessage("100 ms + one outside sample + 100 ms must not arm").That(vm.IsArmed).IsFalse();

        Feed(vm, inside, t, 60);                                   // the fresh run reaches 150 ms
        Check.WithCustomMessage("the restarted run arms on its own 150 ms").That(vm.IsArmed).IsTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void G3_ADroppedSample_ResetsTheRun(bool paused)
    {
        // Mutant M5: gap samples are not dropped-and-reset (they are counted, or skipped without a
        // reset) → the run 0…90, a gap at 120, 150… spans 150 ms and arms, red. The pause gate drops
        // paused samples the same way.
        var vm = NewVm80();
        vm.EnterScrollMode(new PhysicalPoint(1000, 1000), new PhysicalPoint(3000, 600));
        var inside = new PhysicalPoint(1100, 1000);

        Feed(vm, inside, 0, 90);
        var dropped = new GazeFrame(inside, 120_000, IsInGap: !paused);
        vm.Receive(new GazeCursorMovedMessage(dropped) { IsPaused = paused });
        long t = Feed(vm, inside, 150_000, 90);                    // 150…240: 90 ms after the drop

        Check.WithCustomMessage("a dropped sample inside the run must break it").That(vm.IsArmed).IsFalse();

        Feed(vm, inside, t, 60);
        Check.WithCustomMessage("the run after the drop arms on its own 150 ms").That(vm.IsArmed).IsTrue();
    }

    [Fact]
    public void G4_TheRestPoint_IsTheCentroidOfTheArmingFixation()
    {
        // The owner's tracker sits off the cross: 150 px left, 60 px down. Mutants: M6 velocity still
        // computed from the anchor → the sample at the centroid scrolls, red; M7 rest point = the last
        // sample (10 px off the centroid) → RestPoint misses by 10.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var vm = NewVm80();
            double vx = double.NaN, vy = double.NaN;
            vm.TargetVelocityChanged += (x, y) => { vx = x; vy = y; };
            var anchor = new PhysicalPoint(1000, 800);
            vm.EnterScrollMode(anchor, new PhysicalPoint(3000, 400));
            var centre = new PhysicalPoint(anchor.X - 150, anchor.Y + 60);

            long t = 0;
            for (int k = 0; k < 6; k++, t += StepUs)   // 0…150 ms, jitter ±10 px: the mean is the centre
                vm.Receive(new GazeCursorMovedMessage(
                    new PhysicalPoint(centre.X + (k % 2 == 0 ? -10 : 10), centre.Y), t));

            Check.That(vm.IsArmed).IsTrue();
            Check.That(vm.RestPoint.X).IsCloseTo(centre.X, 1.0);
            Check.That(vm.RestPoint.Y).IsCloseTo(centre.Y, 1.0);
            Check.WithCustomMessage("the anchor itself is unchanged").That(vm.Anchor).IsEqualTo(anchor);

            vm.Receive(new GazeCursorMovedMessage(centre, t));
            Check.WithCustomMessage("gaze on the rest point is the neutral position: no scroll")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);
            Check.WithCustomMessage("the dot follows the rest point too").That(vm.AnchorDotOffsetX).IsCloseTo(0, 1e-9);
            Check.That(vm.AnchorDotOffsetY).IsCloseTo(0, 1e-9);

            t += StepUs;
            vm.Receive(new GazeCursorMovedMessage(anchor, t));
            Check.WithCustomMessage("gaze at the anchor is now 162 px right-and-up of the rest point")
                .That(vx).IsStrictlyGreaterThan(0);
            Check.That(vy).IsStrictlyGreaterThan(0);   // up: the rest point is BELOW the anchor on screen
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void G4_NearestPx_FreezesOnceArmed()
    {
        // The diagnostic reports how close the gaze came BEFORE scrolling started; the armed line and
        // every later per-second line must keep that value. Mutant R4: keep updating nearest after
        // arming → the sample on the anchor drags it to 0, red.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var vm = NewVm80();
            var anchor = new PhysicalPoint(1000, 800);
            vm.EnterScrollMode(anchor, new PhysicalPoint(3000, 400));
            var centre = new PhysicalPoint(anchor.X - 150, anchor.Y + 60);

            long t = Arm(vm, centre);
            Check.That(vm.IsArmed).IsTrue();
            double frozen = vm.NearestPx;
            Check.WithCustomMessage("the premise: nearest is the arming fixation's distance, ~162 px")
                .That(frozen).IsCloseTo(centre.DistanceTo(anchor), 1e-9);

            vm.Receive(new GazeCursorMovedMessage(anchor, t));
            Check.WithCustomMessage("a sample ON the anchor after arming must not move nearest")
                .That(vm.NearestPx).IsEqualTo(frozen);

            vm.Receive(new GazeCursorMovedMessage(anchor, t + StepUs));
            Check.That(vm.NearestPx).IsEqualTo(frozen);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void G6_TheDeadZone_IsUnchanged_AndNeverExceedsTheStartCircle(double scale)
    {
        // Mutant M9: the dead zone set to the start radius → 225/450 instead of 80, red.
        DpiHelper.Initialize(scale, scale);
        try
        {
            var vm = NewVm80();
            Check.That(vm.DeadZoneRadiusPx).IsCloseTo(80.0, 1e-9);
            Check.That(vm.DeadZoneRadiusPx <= vm.StartRadiusPx).IsTrue();
            Check.That(vm.StartRadiusPx).IsCloseTo(ScrollOverlayViewModel.StartRadiusDip * scale, 1e-9);
            // The literal, not the constant read back: 225 DIP is what the owner chose (fixup 03), so
            // 175, 250 or an unscaled radius all fail here.
            Check.WithCustomMessage($"scale {scale}: the start circle is 225 DIP in radius")
                .That(vm.StartRadiusPx).IsCloseTo(225.0 * scale, 1e-9);

            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = 10;
            Check.WithCustomMessage("the legacy floor of 30 is unchanged")
                .That(new ScrollOverlayViewModel(new WeakReferenceMessenger(), settings.Scroll, settings.Dwell).DeadZoneRadiusPx)
                .IsCloseTo(30.0, 1e-9);

            settings.Dwell.FixationRadiusPixels = 200;   // the setting's maximum
            var wide = new ScrollOverlayViewModel(new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
            Check.WithCustomMessage("the start circle is never smaller than the dead zone")
                .That(wide.StartRadiusPx).IsCloseTo(Math.Max(200.0, ScrollOverlayViewModel.StartRadiusDip * scale), 1e-9);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void WhileUnarmed_TheDotFeedbackKeepsUpdating()
    {
        // F4 (fixup 01): the dot is the only visual response before arming; the unarmed branch must
        // still move it (G-R2 survived without this). Mutant: drop FeedbackChanged in the unarmed
        // branch → the count stays 0, red.
        var vm = NewVm();
        int feedback = 0;
        vm.FeedbackChanged += () => feedback++;
        vm.EnterScrollMode(SlotAnchor, new PhysicalPoint(3640, 1200));

        vm.Receive(new GazeCursorMovedMessage(ScrollSlot, 0));   // far from the anchor: unarmed

        Check.That(vm.IsArmed).IsFalse();
        Check.That(feedback).IsEqualTo(1);
        Check.That(vm.AnchorDotOffsetX).IsStrictlyLessThan(0);   // dx = 1920 - 2100
        Check.That(vm.AnchorDotOffsetY).IsStrictlyLessThan(0);   // dy = 800 - 1660
    }

    [Fact]
    public void ArmingLogsExactlyOncePerSession_WithTheArmedLine()
    {
        // E5 (F-S5's armed line, emitted where F-S4 arms): once per entry, never before.
        var lines = new List<string>();
        var vm = new ScrollOverlayViewModel(
            new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings(), lines.Add);
        vm.EnterScrollMode(SlotAnchor, new PhysicalPoint(3640, 1200));

        vm.Receive(new GazeCursorMovedMessage(ScrollSlot, 0));
        Check.That(lines).IsEmpty();

        long t = Arm(vm, SlotAnchor, 30_000);
        Check.That(lines).HasSize(1);
        Check.WithCustomMessage($"saw: {string.Join(" | ", lines)}")
            .That(lines[0].StartsWith("Scroll armed after ", StringComparison.Ordinal)).IsTrue();
        // C5: the offset (rest point - anchor) and the nearest approach ride the line; the fixation
        // sat exactly on the anchor, so both are zero.
        Check.That(lines[0].EndsWith(" ms (offset=(0,0)px, nearest=0px)", StringComparison.Ordinal)).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(2100, 900), t));
        Check.That(lines).HasSize(1);
    }

    // ── G7 (scroll start fix, F-S3): the VM's thresholds in physical px ──────

    [Fact]
    public void Thresholds_ArePhysicalPxAtScale2_TheDeadZoneFollowsTheFixationRadius()
    {
        // The owner runs scale 2.0 and R=80 px. The dead zone is max(30, R) = 80 px; the stop zone
        // is the stop button's own radius (50 DIP → 100 px); the dot feedback's 200 DIP → 400 px;
        // the gain is per DIP. Mutants: dead zone back to 30; the / pxPerDip dropped; the stop zone
        // back to 50; MaxOffsetPx unscaled.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = 80;
            var vm = new ScrollOverlayViewModel(
                new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
            var anchor = new PhysicalPoint(1000, 1000);
            var stop = new PhysicalPoint(3000, 600);
            double vx = double.NaN, vy = double.NaN;
            vm.TargetVelocityChanged += (x, y) => { vx = x; vy = y; };
            vm.EnterScrollMode(anchor, stop);
            long t = Arm(vm, anchor);
            Check.That(vm.IsArmed).IsTrue();

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 70, anchor.Y), t));
            Check.WithCustomMessage("70 px is inside the 80-px dead zone")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            // A 100-DIP effective offset: 80 + 200 px from the anchor.
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 280, anchor.Y), t + 30_000));
            Check.WithCustomMessage("the gain must be per DIP at 2.0")
                .That(vx).IsCloseTo(100 * ScrollEngine.DefaultPixelsToNotchesFactor, 1e-9);

            vm.Receive(new GazeCursorMovedMessage(
                new PhysicalPoint(stop.X + 80, stop.Y), t + 60_000));   // the stop button's outer half
            Check.WithCustomMessage("80 px from the stop centre is inside its 100-px zone")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 200, anchor.Y), t + 90_000));
            Check.WithCustomMessage("the dot feedback's range is 400 px at 2.0")
                .That(vm.AnchorDotOffsetX).IsCloseTo(0.5, 1e-9);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void Thresholds_AtScale1_AreTheDipValues()
    {
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = 80;
            var vm = new ScrollOverlayViewModel(
                new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
            var anchor = new PhysicalPoint(1000, 1000);
            var stop = new PhysicalPoint(3000, 600);
            double vx = double.NaN, vy = double.NaN;
            vm.TargetVelocityChanged += (x, y) => { vx = x; vy = y; };
            vm.EnterScrollMode(anchor, stop);
            long t = Arm(vm, anchor);

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 70, anchor.Y), t));
            Check.That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(stop.X + 80, stop.Y), t + 30_000));
            Check.WithCustomMessage("the stop zone is 50 px at 1.0, so 80 px scrolls")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsStrictlyGreaterThan(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
