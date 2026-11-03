namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The task switcher's dedicated close target (the QuickActionsGridViewModel close-button
/// pattern): a dismissal-only dwell target that NEVER sends <see cref="TaskSwitcherSelectionMessage"/>
/// — a non-paused tile dwell always switches windows, so without it a gaze user had no way out
/// of the fullscreen overlay except switching, and an empty enumeration left no exit at all.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherCloseTargetTests
{
    private static readonly PhysicalPoint CloseCenter = new(1000, 1100);
    private static readonly PhysicalPoint AwayFromTargets = new(50, 50);

    private static TaskSwitcherViewModel NewSwitcher(WeakReferenceMessenger messenger, EyeCtrlSettings settings) =>
        new(new OneWindowManager(), settings, messenger);

    [Fact]
    public void UnpausedCloseDwell_Dismisses_WithoutSelecting()
    {
        var messenger = new WeakReferenceMessenger();
        var selections = new List<IntPtr>();
        var recorder = new PauseGateRound2Tests.SelectionRecipient(selections);
        messenger.Register(recorder);
        var settings = new EyeCtrlSettings();
        using var vm = NewSwitcher(messenger, settings);

        vm.Receive(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsTrue();

        // The View registers the close disc after layout; simulate that.
        vm.UpdateCloseButtonPosition(CloseCenter);

        // No tile target is ever registered — gaze rests only on the close disc.
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1000L;
        DwellDrive.Hold(messenger, CloseCenter, 0, dwellUs + 1);

        Check.That(vm.IsVisible).IsFalse();   // dismissed
        Check.That(selections).IsEmpty();     // and no window switch (OS input) fired
    }

    [Fact]
    public void PausedCloseDwell_Dismisses_WithoutSelecting()
    {
        var messenger = new WeakReferenceMessenger();
        var selections = new List<IntPtr>();
        var recorder = new PauseGateRound2Tests.SelectionRecipient(selections);
        messenger.Register(recorder);
        var settings = new EyeCtrlSettings();
        using var vm = NewSwitcher(messenger, settings);

        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateCloseButtonPosition(CloseCenter);

        // Paused samples: the switcher opted into DwellActiveWhilePaused, so the close dwell
        // accumulates and must still dismiss — and NEVER select (paused or not).
        DwellDrive.Hold(0, 900_000, t => messenger.Send(
            new GazeCursorMovedMessage(CloseCenter, t) { IsPaused = true }));

        Check.That(vm.IsVisible).IsFalse();   // escapable: the overlay dismissed
        Check.That(selections).IsEmpty();     // and no window switch (OS input) fired
    }

    [Fact]
    public void CloseTargetProgress_RoutesToCloseDwellProgress()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewSwitcher(messenger, settings);

        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateCloseButtonPosition(CloseCenter);

        // ~150 ms of accumulated dwell: progress must surface on the close observable.
        DwellDrive.Hold(messenger, CloseCenter, 0, 150_000);

        Check.That(vm.CloseDwellProgress).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void CloseTargetPauseChanged_RoutesToCloseDwellPaused()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Dwell.DwellPauseGraceMs = 350;
        using var vm = NewSwitcher(messenger, settings);

        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateCloseButtonPosition(CloseCenter);

        // Build some dwell, then leave within the grace: the held dwell reports paused.
        DwellDrive.Hold(messenger, CloseCenter, 0, 300_000);
        messenger.Send(new GazeCursorMovedMessage(AwayFromTargets, 330_000));
        Check.That(vm.CloseDwellPaused).IsTrue();

        // Returning inside the target resumes it: paused clears.
        messenger.Send(new GazeCursorMovedMessage(CloseCenter, 360_000));
        Check.That(vm.CloseDwellPaused).IsFalse();
    }

    [Fact]
    public void CloseTarget_RegistersTheSharedHitRadius_TheB8Chain()
    {
        // B8: what is drawn IS what the detector hit-tests. The drawn disc is 100 DIP —
        // TaskSwitcherWindow.CloseButtonSize = 2 x UIConstants.ButtonRadius, pinned against the
        // XAML attribute by GlyphSizeCouplingTests (App.Tests; this project cannot see the WPF
        // window). The registered hit radius must therefore be exactly the shared r50 —
        // LogicalToPhysicalX of it — and never an independent number that can drift: a drawn
        // 100 DIP disc against a different hit radius re-splits drawn-vs-hit on this
        // dismissal-only surface (the 2026-08-17 field report's failure class).
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewSwitcher(messenger, settings);

        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateCloseButtonPosition(CloseCenter);

        var target = vm.DetectorForTest.Targets.Single(t => t.Id == TaskSwitcherViewModel.CloseTargetId);
        Check.That(target.Bounds).IsNull();
        Check.That(target.Radius).IsEqualTo(DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius));
    }

    [Fact]
    public void ShowAndHide_ResetCloseDwellState()
    {
        // The VM is a DI singleton (AppBootstrapper): a bar or dim left over from one session
        // must not greet the next show.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewSwitcher(messenger, settings);

        // ShowOverlay resets any stale state from before the show.
        vm.CloseDwellProgress = 0.5;
        vm.CloseDwellPaused = true;
        vm.Receive(new TaskSwitcherToggledMessage());
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();

        // HideOverlay resets again (selection or second toggle path).
        vm.CloseDwellProgress = 0.5;
        vm.CloseDwellPaused = true;
        vm.Receive(new TaskSwitcherToggledMessage());   // shown → toggled off = HideOverlay
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();
    }

    /// <summary>Minimal IWindowManager fake: one window, no effects (DwellPauseGraceWiringTests pattern).</summary>
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
}
