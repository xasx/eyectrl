namespace EyeCtrl.ViewModels.Tests;

using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Issue #172 item 3: every <see cref="DwellOverlayViewModelBase.Receive(GazeCursorMovedMessage)"/>
/// override must stamp <c>LastSampleWasPaused</c> via <c>NoteSamplePaused(message.IsPaused)</c>
/// at the top of the override — before visibility/activity, gap, and <c>SampleMayDwell</c> gates —
/// mirroring <see cref="DwellOverlayViewModelBase.Receive(GazeCursorMovedMessage)"/> and the
/// edge-bar precedent in <see cref="EdgeButtonBarViewModelPauseFlagTests"/>.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DwellOverlayReceivePauseStampTests
{
    private static readonly PhysicalPoint Point = new(500, 500);

    private static GazeCursorMovedMessage Sample(bool paused = false, bool inGap = false, long timestampUs = 1_000_000) =>
        new(new GazeFrame(Point, timestampUs, IsInGap: inGap)) { IsPaused = paused };

    [Fact]
    public void ScrollOverlayViewModel_StampsAndClearsPauseFlag_AcrossVisibleHiddenAndGapSamples()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ScrollOverlayViewModel(messenger, new ScrollSettings(), new DwellSettings());

        // Hidden (!IsVisible): still stamps before the early return, matching base.Receive.
        vm.Receive(Sample(paused: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();

        // Visible: paused sample is gated by SampleMayDwell, yet still stamps the flag.
        vm.EnterScrollMode(Point, new PhysicalPoint(900, 900));
        vm.Receive(Sample(paused: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();

        // In-gap paused sample: stamps before SampleMayDwell returns false.
        vm.Receive(Sample(paused: true, inGap: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();
    }

    [Fact]
    public void ToolbarViewModel_StampsAndClearsPauseFlag_AcrossActiveInactiveAndGapSamples()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ToolbarViewModel(new EyeCtrlSettings(), messenger);

        // Inactive (!DwellEnabled): still stamps before the early return.
        vm.SetActive(false);
        vm.Receive(Sample(paused: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();

        // Active + in-gap paused sample: stamps before the gap/SampleMayDwell gate.
        vm.SetActive(true);
        vm.Receive(Sample(paused: true, inGap: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        // Active unpaused sample clears a stale paused flag.
        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();
    }

    [Fact]
    public void DragTrailViewModel_StampsAndClearsPauseFlag_AcrossInactiveActiveAndGapSamples()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new DragTrailViewModel(new EyeCtrlSettings(), messenger);

        // Inactive (!IsDragActive): still stamps before the early return.
        vm.Receive(Sample(paused: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();

        // Active drag + paused sample: stamps before detector/band routing.
        vm.Receive(new StartDragTrailMessage(Point));
        vm.Receive(Sample(paused: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();

        vm.Receive(Sample(paused: false));
        Check.That(vm.LastSampleWasPausedForTest).IsFalse();

        // Active drag + in-gap paused sample: stamps before SampleMayDwell early return.
        vm.Receive(Sample(paused: true, inGap: true));
        Check.That(vm.LastSampleWasPausedForTest).IsTrue();
    }

    [Fact]
    public void EveryReceiveOverride_IsAccountedFor_AndStampsLastSampleWasPaused()
    {
        var overrides = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Where(t => t.GetMethod(
                nameof(DwellOverlayViewModelBase.Receive),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null,
                types: [typeof(GazeCursorMovedMessage)],
                modifiers: null) is not null)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Check.That(string.Join(",", overrides)).IsEqualTo(
            "BackstageViewModel,DragTrailViewModel,EdgeButtonBarViewModel,FirstRunGuideViewModel," +
            "ResumeStripViewModel,ScrollOverlayViewModel,TaskSwitcherViewModel,ToolbarViewModel");

        foreach (var name in overrides)
        {
            using var vm = BuildOverrideSurface(name);

            vm.Receive(Sample(paused: true));
            Check.WithCustomMessage($"{name}.Receive skipped NoteSamplePaused on a paused sample")
                .That(vm.LastSampleWasPausedForTest).IsTrue();

            vm.Receive(Sample(paused: false));
            Check.WithCustomMessage($"{name}.Receive failed to clear a stale paused flag")
                .That(vm.LastSampleWasPausedForTest).IsFalse();

            vm.Receive(Sample(paused: true, inGap: true));
            Check.WithCustomMessage($"{name}.Receive skipped NoteSamplePaused on an in-gap sample")
                .That(vm.LastSampleWasPausedForTest).IsTrue();
        }
    }

    private static DwellOverlayViewModelBase BuildOverrideSurface(string typeName)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        return typeName switch
        {
            nameof(BackstageViewModel) => new BackstageViewModel(messenger, settings),
            nameof(DragTrailViewModel) => new DragTrailViewModel(settings, messenger),
            nameof(EdgeButtonBarViewModel) => new EdgeButtonBarViewModel(settings, messenger),
            nameof(FirstRunGuideViewModel) => new FirstRunGuideViewModel(
                messenger, settings.Dwell.DwellCooldownMs, settings, sink: null, dwellRegistry: null),
            nameof(ResumeStripViewModel) => new ResumeStripViewModel(settings, messenger),
            nameof(ScrollOverlayViewModel) => new ScrollOverlayViewModel(messenger, settings.Scroll, settings.Dwell),
            nameof(TaskSwitcherViewModel) => new TaskSwitcherViewModel(new EmptyWindowManager(), settings, messenger),
            nameof(ToolbarViewModel) => new ToolbarViewModel(settings, messenger),
            _ => throw new ArgumentOutOfRangeException(nameof(typeName), typeName, "Add the new Receive override here."),
        };
    }

    private sealed class EmptyWindowManager : IWindowManager
    {
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => [];
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
