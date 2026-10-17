namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

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
}
