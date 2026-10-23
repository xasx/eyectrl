namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Adaptation;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// A1 — forgiving dwell on the <c>DwellOverlayViewModelBase</c> family. Three things are pinned:
/// every wired surface's detector carries <see cref="DwellSettings.DwellPauseGraceMs"/> and follows
/// a live change; a held dwell reaches the surface's item as a display-only paused flag (set on
/// leave, cleared on return and on expiry) while the item keeps the held progress; and the scroll
/// overlay, which is not wired, stays at grace 0.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DwellPauseGraceWiringTests
{
    private static readonly PhysicalPoint On = new(500, 500);
    private static readonly PhysicalPoint Off = new(20, 20);
    private const long Ms = 1_000;

    /// <summary>One wired surface, built for a given settings object.</summary>
    private sealed record Surface(
        string Name,
        IDisposable Vm,
        DwellDetector Detector,
        string TargetId,
        Func<bool>? ItemPaused,
        Func<double>? ItemProgress);

    private sealed class OneWindowManager : IWindowManager
    {
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() =>
            new[] { new AppWindow(new IntPtr(4242), "target", 1234, "target") };
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => true;
    }

    private sealed class SingleActionStore(QuickAction action) : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => new[] { action };
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => id == action.Id ? action : null;
    }

    private static DwellDetector DetectorOf(DwellOverlayViewModelBase vm) => vm.DetectorForTest;

    /// <summary>Every surface the ticket wires, names stable for failure messages.</summary>
    public static readonly string[] WiredNames =
    [
        "Toolbar", "ModesMenu", "ActionMenu", "ManualSelection", "GlassBackdrop",
        "EdgeBar", "DragTrail", "TaskSwitcher", "QuickActions",
    ];

    public static TheoryData<string> Wired()
    {
        var data = new TheoryData<string>();
        foreach (var name in WiredNames) data.Add(name);
        return data;
    }

    /// <summary>Surfaces whose items expose the paused flag (the ones with a visible dwell indicator).</summary>
    public static TheoryData<string> WithItems()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "Toolbar", "ModesMenu", "ActionMenu", "EdgeBar", "TaskSwitcher", "QuickActions" })
            data.Add(name);
        return data;
    }

    private static Surface Build(string name, EyeCtrlSettings settings)
    {
        var messenger = new WeakReferenceMessenger();
        switch (name)
        {
            case "Toolbar":
            {
                var vm = new ToolbarViewModel(settings, messenger);
                vm.UpdateButtonScreenPosition("Pause", On);
                var item = vm.Buttons.Single(b => b.Id == "Pause");
                return new Surface(name, vm, DetectorOf(vm), "Pause", () => item.IsDwellPaused, () => item.DwellProgress);
            }
            case "ModesMenu":
            {
                var vm = new ModesMenuViewModel(settings, messenger);
                vm.SetRowScreenPositions([("modes_left", On, 40)]);
                var item = vm.Buttons.Single(b => b.Id == "modes_left");
                return new Surface(name, vm, DetectorOf(vm), "modes_left", () => item.IsDwellPaused, () => item.DwellProgress);
            }
            case "ActionMenu":
            {
                var vm = new ActionMenuViewModel(settings, messenger);
                messenger.Send(new ShowActionMenuMessage(
                    new PhysicalPoint(500, 500), [InteractionAction.LeftClick, InteractionAction.RightClick],
                    false, 600, new PhysicalPoint(500, 500)));
                var item = vm.Items[0];
                return new Surface(name, vm, DetectorOf(vm), item.Id, () => item.IsDwellPaused, () => item.DwellProgress);
            }
            case "ManualSelection":
            {
                var vm = new ManualSelectionOverlayViewModel(settings, messenger);
                return new Surface(name, vm, DetectorOf(vm), "", null, null);
            }
            case "GlassBackdrop":
            {
                var vm = new GlassBackdropViewModel(settings, messenger);
                messenger.Send(new ShowBackdropMessage(On));
                return new Surface(name, vm, DetectorOf(vm), "backdrop_hide", null, () => vm.HideButtonProgress);
            }
            case "EdgeBar":
            {
                var vm = new EdgeButtonBarViewModel(settings, messenger);
                vm.ShowButtons(
                    [new EdgeButtonDefinition("Drag_cancel", "✕", 700, m => m.Send(new DragCancelMessage()), 30)],
                    new PhysicalPoint(1000, 500));
                var item = vm.Buttons.Single(b => b.Id == "Drag_cancel");
                return new Surface(name, vm, DetectorOf(vm), "Drag_cancel", () => item.IsDwellPaused, () => item.DwellProgress);
            }
            case "DragTrail":
            {
                var vm = new DragTrailViewModel(settings, messenger);
                return new Surface(name, vm, DetectorOf(vm), "", null, null);
            }
            case "TaskSwitcher":
            {
                var vm = new TaskSwitcherViewModel(new OneWindowManager(), settings, messenger);
                vm.Receive(new TaskSwitcherToggledMessage());
                vm.UpdateTilePosition(new IntPtr(4242), On);
                var item = vm.Tiles.Single();
                return new Surface(name, vm, DetectorOf(vm), "4242", () => item.IsDwellPaused, () => item.DwellProgress);
            }
            case "QuickActions":
            {
                var action = new QuickAction { Id = "act1", Type = QuickActionType.LaunchExe, Label = "a" };
                var vm = new QuickActionsGridViewModel(new SingleActionStore(action), settings, messenger);
                vm.Receive(new QuickActionsToggledMessage());
                vm.UpdateTilePosition("act1", On);
                var item = vm.Tiles.Single();
                return new Surface(name, vm, DetectorOf(vm), "act1", () => item.IsDwellPaused, () => item.DwellProgress);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    private static void Hold(Surface s, long fromMs, long toMs)
    {
        // The target is addressed through the detector the surface itself owns, so the surface's own
        // event subscriptions (progress and paused) are what carry the result to the item.
        var center = s.Detector.Targets.Single(t => t.Id == s.TargetId).Center;
        for (var t = fromMs; t <= toMs; t += 30)
            s.Detector.Update(center, t * Ms);
    }

    // ------------------------------------------------------------------ (a) the setting reaches each detector

    [Theory]
    [MemberData(nameof(Wired))]
    public void TheWiredSurface_CarriesTheSettingsGrace(string name)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.DwellPauseGraceMs = 350;

        var surface = Build(name, settings);
        using (surface.Vm)
            Check.WithCustomMessage($"{name} does not carry DwellPauseGraceMs")
                .That(surface.Detector.PauseGraceMs).IsEqualTo(350);
    }

    [Theory]
    [MemberData(nameof(Wired))]
    public void TheWiredSurface_ReleasesAHold_WhenTheGazeEntersAnotherTarget(string name)
    {
        // A1 fixup-01 M3: the overlay family's grace forgives empty space and silence, never a
        // neighbour (the straddle Midas path). The detector-level behaviour is pinned in Core.
        var surface = Build(name, new EyeCtrlSettings());
        using (surface.Vm)
            Check.WithCustomMessage($"{name}: the detector does not release holds on another target")
                .That(surface.Detector.OtherTargetReleasesHold).IsTrue();
    }

    // Mutation (A1-m3d): the base constructor drops `OtherTargetReleasesHold = true` -> red.

    // Mutation (A1-w1): remove `settings.Dwell` from ONE surface's base(...) call -> that surface's
    //   case goes red (PauseGraceMs 0).

    [Fact]
    public void TheDefaultSetting_GivesEverySurfaceTheFixationGapBound()
    {
        foreach (var name in WiredNames)
        {
            var surface = Build(name, new EyeCtrlSettings());
            using (surface.Vm)
                Check.WithCustomMessage($"{name}: the shipped default grace did not arrive")
                    .That(surface.Detector.PauseGraceMs).IsEqualTo(FixationDetector.GapAbortMs);
        }
    }

    // ------------------------------------------------------------------ (b) a live slider change

    [Theory]
    [MemberData(nameof(Wired))]
    public void ALiveSettingChange_ReachesTheExistingDetector(string name)
    {
        var settings = new EyeCtrlSettings();
        var surface = Build(name, settings);
        using (surface.Vm)
        {
            settings.Dwell.DwellPauseGraceMs = 200;
            Check.WithCustomMessage($"{name}: a slider change did not reach the live detector")
                .That(surface.Detector.PauseGraceMs).IsEqualTo(200);

            settings.Dwell.DwellPauseGraceMs = 0;
            Check.That(surface.Detector.PauseGraceMs).IsEqualTo(0);
        }
    }

    // Mutation (A1-w2): drop the PropertyChanged subscription / SetPauseGrace call -> red.

    [Fact]
    public void AnUnrelatedSettingChange_LeavesTheGraceAlone()
    {
        var settings = new EyeCtrlSettings();
        var surface = Build("Toolbar", settings);
        using (surface.Vm)
        {
            settings.Dwell.DwellCooldownMs = 700;
            Check.That(surface.Detector.PauseGraceMs).IsEqualTo(500);
        }
    }

    [Fact]
    public void ADisposedSurface_StopsFollowingTheSetting()
    {
        var settings = new EyeCtrlSettings();
        var surface = Build("Toolbar", settings);
        surface.Vm.Dispose();

        settings.Dwell.DwellPauseGraceMs = 100;

        Check.WithCustomMessage("a disposed surface must unsubscribe, or the settings object keeps it alive")
            .That(surface.Detector.PauseGraceMs).IsEqualTo(500);
    }

    // Mutation (A1-w3): OnDispose forgets the unsubscribe -> red.

    [Fact]
    public void TheScrollOverlay_StaysAtGraceZero_EvenWhenTheSettingChanges()
    {
        var settings = new EyeCtrlSettings();
        using var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
        Check.That(DetectorOf(vm).PauseGraceMs).IsEqualTo(0);

        settings.Dwell.DwellPauseGraceMs = 800;

        Check.WithCustomMessage("the scroll overlay is out of A1's scope (ScrollModeController is owned elsewhere)")
            .That(DetectorOf(vm).PauseGraceMs).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ (d) the paused signal reaches the item

    [Theory]
    [MemberData(nameof(WithItems))]
    public void LeavingMidDwell_HoldsTheItemProgress_AndMarksItPaused_ThenResumeClearsIt(string name)
    {
        var surface = Build(name, new EyeCtrlSettings());
        using (surface.Vm)
        {
            Hold(surface, 0, 180);
            double before = surface.ItemProgress!();
            Check.That(before).IsStrictlyGreaterThan(0.0);
            Check.That(surface.ItemPaused!()).IsFalse();

            surface.Detector.Update(Off, 210 * Ms);

            Check.WithCustomMessage($"{name}: the item was not marked paused on leave").That(surface.ItemPaused!()).IsTrue();
            Check.WithCustomMessage($"{name}: the held progress was dropped on leave")
                .That(surface.ItemProgress!()).IsCloseTo(before, 0.2);
            Check.That(surface.ItemProgress!()).IsStrictlyGreaterThan(0.0);

            var center = surface.Detector.Targets.Single(t => t.Id == surface.TargetId).Center;
            surface.Detector.Update(center, 300 * Ms);

            Check.WithCustomMessage($"{name}: resume did not clear the paused flag").That(surface.ItemPaused!()).IsFalse();
        }
    }

    // Mutation (A1-w4): remove the OnTargetPauseChanged override at one surface -> that case goes red.

    [Theory]
    [MemberData(nameof(WithItems))]
    public void AnExpiredPause_ClearsTheFlag_AndDropsTheItemProgress(string name)
    {
        var surface = Build(name, new EyeCtrlSettings());
        using (surface.Vm)
        {
            Hold(surface, 0, 180);
            surface.Detector.Update(Off, 210 * Ms);
            Check.That(surface.ItemPaused!()).IsTrue();

            surface.Detector.Update(Off, 900 * Ms);   // past the 500 ms grace

            Check.WithCustomMessage($"{name}: an expired pause left the item flagged").That(surface.ItemPaused!()).IsFalse();
            Check.WithCustomMessage($"{name}: an expired pause left the progress frozen").That(surface.ItemProgress!()).IsEqualTo(0.0);
        }
    }

    [Theory]
    [MemberData(nameof(WithItems))]
    public void WithGraceOff_LeavingResetsAtOnce_AndNeverMarksPaused(string name)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.DwellPauseGraceMs = 0;
        var surface = Build(name, settings);
        using (surface.Vm)
        {
            Hold(surface, 0, 180);
            surface.Detector.Update(Off, 210 * Ms);

            Check.That(surface.ItemPaused!()).IsFalse();
            Check.WithCustomMessage($"{name}: grace 0 must reset on leave, exactly as before")
                .That(surface.ItemProgress!()).IsEqualTo(0.0);
        }
    }

    [Fact]
    public void TheHideButton_HoldsItsProgress_WhileTheGazeIsAway()
    {
        var surface = Build("GlassBackdrop", new EyeCtrlSettings());
        using (surface.Vm)
        {
            Hold(surface, 0, 180);
            double before = surface.ItemProgress!();
            surface.Detector.Update(Off, 210 * Ms);

            Check.That(surface.ItemProgress!()).IsStrictlyGreaterThan(0.0);
            Check.That(surface.ItemProgress!()).IsCloseTo(before, 0.2);
        }
    }

    [Fact]
    public void TheToolbarHighlight_FollowsTheGaze_NotTheHeldProgress()
    {
        var settings = new EyeCtrlSettings();
        var vm = new ToolbarViewModel(settings, new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");
        vm.UpdateButtonScreenPosition("Pause", On);

        vm.Receive(new GazeCursorMovedMessage(On, 0));
        vm.Receive(new GazeCursorMovedMessage(On, 30_000));
        Check.That(button.IsHighlighted).IsTrue();

        vm.Receive(new GazeCursorMovedMessage(Off, 60_000));

        Check.WithCustomMessage("the acquired highlight must drop when the gaze leaves, even though the arc is held")
            .That(button.IsHighlighted).IsFalse();
        Check.That(button.IsDwellPaused).IsTrue();
        Check.That(button.DwellProgress).IsStrictlyGreaterThan(0.0);

        vm.Receive(new GazeCursorMovedMessage(On, 90_000));
        vm.Receive(new GazeCursorMovedMessage(On, 120_000));
        Check.That(button.IsHighlighted).IsTrue();
        Check.That(button.IsDwellPaused).IsFalse();
    }

    [Fact]
    public void OnTheToolbar_OnlyTheAdaptationResetTarget_IsExemptFromTheGrace()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.UpdateButtonScreenPosition(ResetPreviewEngine.ResetId, On);
        vm.UpdateButtonScreenPosition("Pause", new PhysicalPoint(900, 900));

        var targets = DetectorOf(vm).Targets;
        Check.WithCustomMessage("Reset is a destructive confirm: its pass after a preview must start from zero")
            .That(targets.Single(t => t.Id == ResetPreviewEngine.ResetId).PauseGraceExempt).IsTrue();
        Check.That(targets.Single(t => t.Id == "Pause").PauseGraceExempt).IsFalse();
    }

    // Mutation (A1-x3): drop the PauseGraceExempt initialiser in ToolbarViewModel -> red here and in P16.

    [Fact]
    public void TheQuickActionsCloseButton_GetsItsOwnPausedFlag()
    {
        var action = new QuickAction { Id = "act1", Type = QuickActionType.LaunchExe, Label = "a" };
        var vm = new QuickActionsGridViewModel(new SingleActionStore(action), new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Receive(new QuickActionsToggledMessage());
        vm.UpdateCloseButtonPosition(On);
        var detector = DetectorOf(vm);

        for (var t = 0; t <= 180; t += 30) detector.Update(On, t * Ms);
        detector.Update(Off, 210 * Ms);

        Check.That(vm.CloseDwellPaused).IsTrue();
        Check.That(vm.CloseDwellProgress).IsStrictlyGreaterThan(0.0);

        detector.Update(Off, 900 * Ms);
        Check.That(vm.CloseDwellPaused).IsFalse();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0.0);
    }
}
