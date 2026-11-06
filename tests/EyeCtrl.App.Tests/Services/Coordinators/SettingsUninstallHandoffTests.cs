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
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C9 S6e fixup-08: the Settings window's half of the uninstall hand-off. Settings hides only
/// while Setup's own Q8 is up (it is band-2 topmost, Q8 can never be raised above it), releasing
/// the claims it holds while open so the toolbar and the summon stay usable, and it comes back on
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
        public bool ThrowOnShow { get; set; }
        public bool ThrowOnlyOnce { get; set; }

        public void Hide()
        {
            HideCalls++;
            IsVisible = false;
        }

        public void ShowOnAdvanced()
        {
            ShowOnAdvancedCalls++;
            if (ThrowOnShow)
            {
                ThrowOnShow = !ThrowOnlyOnce;
                throw new InvalidOperationException("show failed");
            }

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
        public ToolbarLifecycleCoordinator Toolbar { get; }
        public ToolbarAutoHideController AutoHide { get; }
        public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
        public WeakReferenceMessenger Messenger { get; } = new();

        public Harness()
        {
            var settings = new EyeCtrlSettings();
            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                Settings = settings,
                Dispatcher = Dispatcher,
                Messenger = Messenger,
                CursorVM = new CursorCircleViewModel(settings, Messenger) { IsVisible = true },
                OverlayManager = TestFakes.MakeOverlayManager(
                    cursorCircle: Ring,
                    trackStatus: new TestFakes.FakeTrackStatusOverlay()),
            };

            QuickActions = new QuickActionsCoordinator(Modal);
            QuickActions.InjectSummonForTesting(
                new QuickActionsSummonController("left", 1920, 1080, 400, 5000));

            // A real auto-hide controller, so the toolbar's "settings" suspension is observable on
            // both edges (a null controller reports every state as suspended, which made the
            // hide's release untestable — review-08 M2).
            AutoHide = new ToolbarAutoHideController(
                enabled: true, edge: "Top", screenW: 1920, screenH: 1080,
                toolbarRect: new PhysicalRect(0, 0, 1920, 48),
                hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
            Toolbar = new ToolbarLifecycleCoordinator(Modal);
            Toolbar.InjectAutoHideForTesting(AutoHide);

            Coordinator = new SettingsUiCoordinator(
                Modal,
                QuickActions,
                Toolbar,
                new KeyboardCoordinator(Modal, QuickActions, Toolbar));
            Coordinator.Initialize(context);
            QuickActions.Initialize(context);
            Modal.SettingsOpenChanged = open => SettingsOpenEvents.Add(open);
        }

        /// <summary>
        /// The open Settings window as production has it: the real open path's claims are TAKEN
        /// (SettingsOpen + its event, the toolbar's auto-hide suspension, the summon claim, the
        /// ring hidden), and the window handle is attached. The real window cannot be constructed
        /// here, so the handle is a recorder.
        /// </summary>
        public Harness Open()
        {
            Modal.SettingsOpen = true;
            Modal.SettingsOpenChanged?.Invoke(true);
            Toolbar.SetAutoHideSuspended("settings", true);
            QuickActions.SetSummonSuspended("settings", true);
            Ring.Hide();
            SettingsOpenEvents.Clear();
            Coordinator.SetHandoffWindowForTests(Window);
            return this;
        }

        /// <summary>The owner's real Settings entry: the toolbar/tray/keyboard send this message.</summary>
        public void RequestSettings() => Messenger.Send(new SettingsRequestedMessage());

        /// <summary>Drains the dispatcher so the coordinator's registered handler runs.</summary>
        public void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose()
        {
            Coordinator.Dispose();
            QuickActions.Dispose();
        }
    }

    private static void AssertOpenClaims(Harness c, bool open, string because)
    {
        Check.WithCustomMessage($"{because}: SettingsOpen")
            .That(c.Modal.SettingsOpen).IsEqualTo(open);
        Check.WithCustomMessage($"{because}: the toolbar's auto-hide suspension")
            .That(c.Toolbar.IsAutoHideSuspended).IsEqualTo(open);
        Check.WithCustomMessage($"{because}: the quick-actions summon claim")
            .That(c.QuickActions.IsSummonSuspended).IsEqualTo(open);
    }

    [Fact]
    public void Hiding_ReleasesTheClaimsSettingsHeld_AndShowsTheRingAgain()
    {
        using var c = new Harness().Open();
        AssertOpenClaims(c, open: true, "the open window");

        var hidden = c.Coordinator.HideSettingsForUninstallHandoff();

        Check.That(hidden).IsTrue();
        Check.That(c.Window.HideCalls).IsEqualTo(1);
        Check.That(c.Window.IsVisible).IsFalse();
        AssertOpenClaims(c, open: false, "the hidden window");
        Check.That(c.SettingsOpenEvents).ContainsOnlyElementsThatMatch(open => !open);
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
        AssertOpenClaims(c, open: true, "the restored window");
        Check.WithCustomMessage("the restore did not announce Settings as open again")
            .That(c.SettingsOpenEvents[^1]).IsTrue();
        Check.That(c.Ring.HideCalls).IsEqualTo(2);   // once on Open, once on the restore
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
        AssertOpenClaims(c, open: false, "a completed uninstall");
    }

    [Fact]
    public void AManualReopenThroughTheRealEntry_ShowsItOnce_AndTheLaterRestoreDoesNothing()
    {
        // Review-08 M3: the request goes through ShowSettingsWindow, the one line the toolbar,
        // tray and keyboard row use — not the helper.
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();

        c.RequestSettings();
        c.Pump();

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
        Check.That(c.Window.IsVisible).IsTrue();
        AssertOpenClaims(c, open: true, "the owner's reopen");

        // Setup ending later must not re-show or re-navigate.
        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
        Check.That(c.Coordinator.ReopenHiddenSettingsWindowForUninstallHandoff()).IsFalse();
        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
    }

    [Fact]
    public void AFailedShow_KeepsTheHiddenFlag_AndTheNextEntryRetries()
    {
        // Review-08 L1: a show that throws must not clear the flag, or a later Settings request
        // would leave the owner with no window at all.
        using var c = new Harness().Open();
        c.Coordinator.HideSettingsForUninstallHandoff();
        c.Window.ThrowOnShow = true;
        c.Window.ThrowOnlyOnce = true;

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);
        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(1);
        Check.That(c.Window.IsVisible).IsFalse();

        c.RequestSettings();
        c.Pump();

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(2);
        Check.That(c.Window.IsVisible).IsTrue();
        AssertOpenClaims(c, open: true, "the retried reopen");
    }

    [Fact]
    public void ARestoreWithNothingHidden_DoesNothing()
    {
        using var c = new Harness().Open();

        c.Coordinator.RestoreSettingsAfterUninstallHandoff(
            UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);

        Check.That(c.Window.ShowOnAdvancedCalls).IsEqualTo(0);
        Check.That(c.Ring.HideCalls).IsEqualTo(1);   // only the open path's
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
        AssertOpenClaims(c, open: false, "a closed window");
    }
}
