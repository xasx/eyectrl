namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels;

/// <summary>
/// C9 S6e fixup-08: the Settings window's half of the uninstall hand-off. Settings hides only
/// while Setup's own Q8 is up (it is band-2 topmost, Q8 can never be raised above it), releasing
/// the claims it holds while open so the toolbar and ring stay usable, and it comes back on
/// „Erweitert" for every end that does not remove EyeCtrl. Never for an uninstall; never twice
/// (the owner may reopen it manually while Q8 is up). All window calls go through a recorder: a
/// test may never show a real window.
/// </summary>
public class SettingsUninstallHandoffTests
{
    private sealed class RecordingWindowHandle : ISettingsWindowHandle
    {
        public bool IsVisible { get; private set; } = true;
        public int HideCalls { get; private set; }
        public int ShowOnAdvancedCalls { get; private set; }

        public void Hide()
        {
            HideCalls++;
            IsVisible = false;
        }

        public void ShowOnAdvanced()
        {
            ShowOnAdvancedCalls++;
            IsVisible = true;
        }
    }

    private sealed class RecordingRing : ICursorCircleOverlay
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }

        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPausedDim(bool dimmed) { }
        public void SetPinned(bool pinned) { }
    }

    private sealed class Harness : IDisposable
    {
        public ModalInteractionState Modal { get; } = new();
        public RecordingRing Ring { get; } = new();
        public RecordingWindowHandle Window { get; } = new();
        public List<bool> SettingsOpenEvents { get; } = [];
        public SettingsUiCoordinator Coordinator { get; }
        public QuickActionsCoordinator QuickActions { get; }
        public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;

        public Harness()
        {
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                Settings = settings,
                Dispatcher = Dispatcher,
                Messenger = messenger,
                CursorVM = new CursorCircleViewModel(settings, messenger) { IsVisible = true },
                OverlayManager = TestFakes.MakeOverlayManager(
                    cursorCircle: Ring,
                    trackStatus: new TestFakes.FakeTrackStatusOverlay()),
            };

            QuickActions = new QuickActionsCoordinator(Modal);
            QuickActions.InjectSummonForTesting(
                new QuickActionsSummonController("left", 1920, 1080, 400, 5000));
            Coordinator = new SettingsUiCoordinator(
                Modal,
                QuickActions,
                new ToolbarLifecycleCoordinator(Modal),
                new KeyboardCoordinator(
                    Modal, new QuickActionsCoordinator(Modal), new ToolbarLifecycleCoordinator(Modal)));
            Coordinator.Initialize(context);
            QuickActions.Initialize(context);
            Modal.SettingsOpenChanged = open => SettingsOpenEvents.Add(open);
        }

        /// <summary>The open Settings window as production has it: the flag set, the handle
        /// attached. The real window cannot be constructed here, so the handle is the record.</summary>
        public Harness Open()
        {
            Modal.SettingsOpen = true;
            SettingsOpenEvents.Clear();
            Coordinator.SetHandoffWindowForTests(Window);
            return this;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            QuickActions.Dispose();
        }
    }

    [Fact]
    public void Hiding_ReleasesTheClaimsSettingsHeld_AndShowsTheRingAgain()
    {
        using var c = new Harness().Open();

        var hidden = c.Coordinator.HideSettingsForUninstallHandoff();

        Check.That(hidden).IsTrue();
        Check.That(c.Window.HideCalls).IsEqualTo(1);
        Check.That(c.Window.IsVisible).IsFalse();
        Check.That(c.Modal.SettingsOpen).IsFalse();
        Check.That(c.SettingsOpenEvents).ContainsOnlyElementsThatMatch(open => !open);
        Check.WithCustomMessage("Settings' summon claim survived the hide: the quick-actions summon stays dead")
            .That(c.QuickActions.IsSummonSuspended).IsFalse();
        Check.That(c.Ring.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void AHiddenWindow_CannotBeHiddenTwice()
    {
        using var c = new Harness().Open();

        Check.That(c.Coordinator.HideSettingsForUninstallHandoff()).IsTrue();
        Check.That(c.Coordinator.HideSettingsForUninstallHandoff()).IsFalse();

        Check.That(c.Window.HideCalls).IsEqualTo(1);
        Check.That(c.Ring.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void WithoutAVisibleWindow_ThereIsNothingToHide()
    {
        using var c = new Harness();
        c.Coordinator.SetHandoffWindowForTests(null);

        Check.That(c.Coordinator.HideSettingsForUninstallHandoff()).IsFalse();
        Check.That(c.SettingsOpenEvents).IsEmpty();
        Check.That(c.Ring.ShowCalls).IsEqualTo(0);
    }

    [Theory]
    [InlineData(UninstallHandoffEnd.NotUninstalled)]
    [InlineData(UninstallHandoffEnd.Unknown)]
    public void EveryNonUninstallEnd_BringsSettingsBackOnAdvanced_AndRetakesTheClaims(
        UninstallHandoffEnd end)
    {
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(end, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
        Check.That(c.Window.IsVisible).IsTrue();
        Check.That(c.Modal.SettingsOpen).IsTrue();
        Check.WithCustomMessage("the restore did not announce Settings as open again")
            .That(c.SettingsOpenEvents[^1]).IsTrue();
        Check.That(c.QuickActions.IsSummonSuspended).IsTrue();
        Check.That(c.Ring.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void AnUninstalledEnd_LeavesSettingsHidden()
    {
        // Setup stops the App in the same run. A window raised over the removal would be the last
        // thing the owner sees.
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.Uninstalled, SetupOutcome.Uninstalled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(0);
        Check.That(c.Window.IsVisible).IsFalse();
        Check.That(c.Modal.SettingsOpen).IsFalse();
    }

    [Fact]
    public void AManualReopen_ShowsItOnce_AndTheLaterRestoreDoesNothing()
    {
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();

        Check.That(c.Coordinator.ReopenHiddenSettingsWindowForUninstallHandoff()).IsTrue();
        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);

        // The owner is back on „Erweitert"; Setup ending later must not re-show or re-navigate.
        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
        Check.That(c.Coordinator.ReopenHiddenSettingsWindowForUninstallHandoff()).IsFalse();
        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
    }

    [Fact]
    public void ARestoreWithNothingHidden_DoesNothing()
    {
        using var c = new Harness().Open();

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(0);
        Check.That(c.Ring.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void AWindowClosedWhileHidden_IsNotShownByTheLaterRestore()
    {
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();

        // The tray's close path (or App shutdown) closes the window during Q8.
        c.Coordinator.OnSettingsWindowClosed(null, EventArgs.Empty);

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(0);
        Check.That(c.Window.IsVisible).IsFalse();
        Check.That(c.Modal.SettingsOpen).IsFalse();
    }
}
