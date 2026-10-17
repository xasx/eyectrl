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
        vm.Receive(new GazeCursorMovedMessage(Anchor, 0));   // F-S4: the first neutral-zone visit arms
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 600), 0));
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

        // Inside the dead zone but NOT on the anchor: a visit, not an exact hit.
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(SlotAnchor.X + 20, SlotAnchor.Y), 30_000));

        Check.That(vm.IsArmed).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(ScrollSlot, 60_000));   // armed: the slot drives now

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

        vm.Receive(new GazeCursorMovedMessage(SlotAnchor, 0));       // the first sample arms
        Check.That(vm.IsArmed).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(2100, 1200), 30_000));

        Check.That(vy).IsStrictlyLessThan(0);                        // follows immediately, as today
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Arming_RadiusFollowsTheFixationRadius_InPhysicalPx(double scale)
    {
        // F3 (fixup 01): pin the arming radius at R = 80 — a sample 60–79 px from the anchor arms;
        // 81+ px does not (the dead zone is physical px, never DPI-converted, so the same values
        // hold at both scales). Mutant: arm on the legacy 30-px constant (G-R1) → the 60/79 cases
        // stay unarmed, red.
        DpiHelper.Initialize(scale, scale);
        try
        {
            foreach (var (offset, expected) in new[] { (60.0, true), (79.0, true), (81.0, false) })
            {
                var settings = new EyeCtrlSettings();
                settings.Dwell.FixationRadiusPixels = 80;
                var vm = new ScrollOverlayViewModel(
                    new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
                vm.EnterScrollMode(new PhysicalPoint(1000, 1000), new PhysicalPoint(3000, 600));

                vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000 + offset, 1000), 0));

                Check.WithCustomMessage(
                        $"scale {scale}: a sample {offset} px from the anchor: armed={vm.IsArmed}, "
                        + $"expected {expected}")
                    .That(vm.IsArmed).IsEqualTo(expected);
            }
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
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

        vm.Receive(new GazeCursorMovedMessage(SlotAnchor, 30_000));
        Check.That(lines).HasSize(1);
        Check.WithCustomMessage($"saw: {string.Join(" | ", lines)}")
            .That(lines[0].StartsWith("Scroll armed after ", StringComparison.Ordinal)).IsTrue();
        Check.That(lines[0].EndsWith(" ms", StringComparison.Ordinal)).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(2100, 900), 60_000));
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
            vm.Receive(new GazeCursorMovedMessage(anchor, 0));
            Check.That(vm.IsArmed).IsTrue();

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 70, anchor.Y), 30_000));
            Check.WithCustomMessage("70 px is inside the 80-px dead zone")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            // A 100-DIP effective offset: 80 + 200 px from the anchor.
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 280, anchor.Y), 60_000));
            Check.WithCustomMessage("the gain must be per DIP at 2.0")
                .That(vx).IsCloseTo(100 * ScrollEngine.DefaultPixelsToNotchesFactor, 1e-9);

            vm.Receive(new GazeCursorMovedMessage(
                new PhysicalPoint(stop.X + 80, stop.Y), 90_000));   // the stop button's outer half
            Check.WithCustomMessage("80 px from the stop centre is inside its 100-px zone")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 200, anchor.Y), 120_000));
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
            vm.Receive(new GazeCursorMovedMessage(anchor, 0));

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(anchor.X + 70, anchor.Y), 30_000));
            Check.That(Math.Abs(vx) + Math.Abs(vy)).IsCloseTo(0, 1e-6);

            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(stop.X + 80, stop.Y), 60_000));
            Check.WithCustomMessage("the stop zone is 50 px at 1.0, so 80 px scrolls")
                .That(Math.Abs(vx) + Math.Abs(vy)).IsStrictlyGreaterThan(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
