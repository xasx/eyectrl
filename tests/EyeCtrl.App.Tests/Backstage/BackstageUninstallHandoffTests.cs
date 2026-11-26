namespace EyeCtrl.App.Tests.Backstage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Ticket 04's uninstall hand-off, at the coordinator that now owns it (migrated from the Settings
/// coordinator): hide releases every claim so Setup's uiAccess=false Q8 is reachable; the watch
/// restores the SAME window on <c>NotUninstalled</c>/<c>Unknown</c>; <c>Uninstalled</c> stays
/// hidden; the owner's manual reopen cancels the watch so its later end cannot show a second time;
/// closing while hidden cancels the watch too. Every window call goes through a prepared
/// <see cref="BackstageWindow"/>, so no test paints a window.
/// </summary>
[Collection("WpfApplication")]
public class BackstageUninstallHandoffTests
{
    private sealed class FakeGateways : ISetupGateways
    {
        public bool Installed => true;
        public bool PreviousAvailable => false;
        public string? PreviousVersion => null;
        public bool PolicyBlocks => false;
        public SetupUpdateOffer? UpdateOffer => null;
        public bool UpdateOfferPending => false;
        public void BeginUpdateOfferProbe(Action whenDone) { }
        public SetupHandoffStart StartUpdateHandoff(SetupUpdateOffer offer) => SetupHandoffStart.Failed;
        public SetupHandoffStart StartRollbackHandoff() => SetupHandoffStart.Failed;
        public SetupHandoffStart StartUninstallHandoff() => SetupHandoffStart.Started;
    }

    private sealed class FakeWatch : IUninstallHandoffWatch
    {
        public bool Disposed { get; private set; }
        public DateTimeOffset StartedAt { get; private set; }
        public Action<UninstallHandoffEnd, SetupOutcome?>? Ended { get; private set; }

        public void Start(DateTimeOffset handoffStartedAt, Action<UninstallHandoffEnd, SetupOutcome?> ended)
        {
            StartedAt = handoffStartedAt;
            Ended = ended;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWatchFactory : IUninstallHandoffWatchFactory
    {
        public FakeWatch Watch { get; } = new();
        public IUninstallHandoffWatch Create() => Watch;
    }

    private sealed class FakeWindowManager : IWindowManager
    {
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => Array.Empty<AppWindow>();
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }

    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }

    private sealed class RecordingCursorCircle : ICursorCircleOverlay
    {
        public int Shows { get; private set; }
        public int Hides { get; private set; }
        public void Show() => Shows++;
        public void Hide() => Hides++;
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) { }
    }

    private sealed class Harness : IDisposable
    {
        public EyeCtrlSettings Settings { get; }
        public ModalInteractionState Modal { get; } = new();
        public WeakReferenceMessenger Messenger { get; } = new();
        public TestFakes.FakeEscapeHatch Escape { get; } = new();
        public RecordingCursorCircle Cursor { get; } = new();
        public TestFakes.FakeToolbarOverlay ToolbarOverlay { get; } = new() { OnScreen = true };
        public CursorCircleViewModel CursorVm { get; }
        public ToolbarViewModel ToolbarVm { get; }
        public KeyboardViewModel KeyboardVm { get; }
        public ModesMenuViewModel ModesVm { get; }
        public TaskSwitcherViewModel TaskSwitcherVm { get; }
        public QuickActionsGridViewModel QuickActionsVm { get; }
        public ActionMenuViewModel ActionMenuVm { get; }
        public BackstageViewModel BackstageVm { get; }
        public BackstageUiCoordinator Coordinator { get; }
        public QuickActionsCoordinator QuickActions { get; }
        public ToolbarLifecycleCoordinator Toolbar { get; }
        public FakeWatchFactory Watches { get; } = new();

        public Harness()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            Settings = new EyeCtrlSettings();
            CursorVm = new CursorCircleViewModel(Settings, Messenger) { IsVisible = true };
            ToolbarVm = new ToolbarViewModel(Settings, Messenger);
            KeyboardVm = new KeyboardViewModel(Settings, Messenger);
            ModesVm = new ModesMenuViewModel(Settings, Messenger);
            TaskSwitcherVm = new TaskSwitcherViewModel(new FakeWindowManager(), Settings, Messenger);
            QuickActionsVm = new QuickActionsGridViewModel(new FakeQuickActionsStore(), Settings, Messenger);
            ActionMenuVm = new ActionMenuViewModel(Settings, Messenger);
            BackstageVm = new BackstageViewModel(Messenger, Settings);

            var overlayManager = TestFakes.MakeOverlayManager(
                cursorCircle: Cursor,
                toolbar: ToolbarOverlay,
                glassBackdrop: new TestFakes.FakeGlassBackdropOverlay());

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                Messenger = Messenger,
                CursorVM = CursorVm,
                ToolbarVM = ToolbarVm,
                ActionMenuVM = ActionMenuVm,
                ModesMenuVM = ModesVm,
                TaskSwitcherVM = TaskSwitcherVm,
                QuickActionsGridVM = QuickActionsVm,
                BackstageVM = BackstageVm,
                OverlayManager = overlayManager,
                GazeEngine = new GazeEngine(
                    new TestFakes.FakeGazeProvider(), new TestFakes.FakeInputSimulator(), Settings),
                InteractionManager = new InteractionManager(),
                Settings = Settings,
                Dispatcher = Dispatcher.CurrentDispatcher,
                EscapeHatch = Escape,
            };

            QuickActions = new QuickActionsCoordinator(Modal);
            QuickActions.InjectSummonForTesting(
                new QuickActionsSummonController("left", 1920, 1080, 400, 5000));
            Toolbar = new ToolbarLifecycleCoordinator(Modal);
            Toolbar.InjectAutoHideForTesting(new ToolbarAutoHideController(
                enabled: true, edge: "Top", screenW: 1920, screenH: 1080,
                toolbarRect: new PhysicalRect(0, 0, 1920, 48),
                hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50));

            Coordinator = new BackstageUiCoordinator(
                Modal, QuickActions, Toolbar, () => false);
            Coordinator.CreateWindowForTest = static () => InvisibleTestWindow.Prepare(new BackstageWindow());
            Coordinator.SetShutdown(() => { });
            Coordinator.SetSetupGateways(() => new FakeGateways());
            Coordinator.SetUninstallHandoffWatches(Watches);
            Coordinator.Initialize(context);
            QuickActions.Initialize(context);
            // ToolbarLifecycleCoordinator is deliberately NOT initialized here: it registers the
            // real gaze routing, which needs overlays this focused harness does not build. Its
            // auto-hide suspension claim set is independent of Initialize, so the injected
            // controller still reports the claim truthfully.
        }

        public void Show() => Coordinator.ShowBackstage();

        /// <summary>Drives the real uninstall card &amp; confirmation, so the presenter starts.</summary>
        public void StartUninstall()
        {
            BackstageVm.ActivateForMouse(BackstageViewModel.UninstallCardId);
            BackstageVm.ActivateForMouse(BackstageWindow.UninstallConfirmChoiceId);
        }

        public void Pump() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose()
        {
            Coordinator.Dispose();
            QuickActions.Dispose();
            BackstageVm.Dispose();
            ToolbarVm.Dispose();
            KeyboardVm.Dispose();
            ActionMenuVm.Dispose();
            CursorVm.Dispose();
        }
    }

    private static void AssertHiddenClaims(Harness h, string because)
    {
        Check.WithCustomMessage($"{because}: the menu must be off screen")
            .That(h.Coordinator.WindowForTest!.IsVisible).IsFalse();
        Check.WithCustomMessage($"{because}: ModalInteractionState.BackstageOpen")
            .That(h.Modal.BackstageOpen).IsFalse();
        Check.WithCustomMessage($"{because}: the toolbar auto-hide suspension")
            .That(h.Toolbar.IsAutoHideSuspended).IsFalse();
        Check.WithCustomMessage($"{because}: the quick-actions summon claim")
            .That(h.QuickActions.IsSummonSuspended).IsFalse();
    }

    private static void AssertOpenClaims(Harness h, string because)
    {
        Check.WithCustomMessage($"{because}: the menu must be on screen")
            .That(h.Coordinator.WindowForTest!.IsVisible).IsTrue();
        Check.WithCustomMessage($"{because}: ModalInteractionState.BackstageOpen")
            .That(h.Modal.BackstageOpen).IsTrue();
        Check.WithCustomMessage($"{because}: the toolbar auto-hide suspension")
            .That(h.Toolbar.IsAutoHideSuspended).IsTrue();
        Check.WithCustomMessage($"{because}: the quick-actions summon claim")
            .That(h.QuickActions.IsSummonSuspended).IsTrue();
    }

    [StaFact]
    public void HidingForUninstall_ReleasesEveryScreenClaim_AndShowsTheRing()
    {
        using var h = new Harness();
        h.Show();
        AssertOpenClaims(h, "the open menu");

        h.StartUninstall();

        Check.That(h.Coordinator.HandoffHiddenForTest).IsTrue();
        Check.That(h.Watches.Watch.Disposed).IsFalse();
        AssertHiddenClaims(h, "the hidden menu");
        // The ring was hidden on open; the hide brings it back so the owner keeps a usable pointer.
        Check.That(h.Cursor.Shows).IsEqualTo(1);
        Check.That(h.Cursor.Hides).IsEqualTo(1);
    }

    [StaTheory]
    [InlineData(UninstallHandoffEnd.NotUninstalled)]
    [InlineData(UninstallHandoffEnd.Unknown)]
    public void EveryNonUninstallEnd_RestoresTheSameWindow_AndRetakesTheClaims(
        UninstallHandoffEnd end)
    {
        using var h = new Harness();
        h.Show();
        h.StartUninstall();
        var window = h.Coordinator.WindowForTest;
        AssertHiddenClaims(h, "the hidden menu");

        h.Watches.Watch.Ended!.Invoke(end, SetupOutcome.Cancelled);

        Check.That(h.Coordinator.HandoffHiddenForTest).IsFalse();
        Check.WithCustomMessage("the restore created a different window")
            .That(h.Coordinator.WindowForTest).IsSameReferenceAs(window);
        AssertOpenClaims(h, "the restored menu");
        Check.That(h.Cursor.Hides).IsEqualTo(2);   // once on open, once on the restore
        Check.That(h.Watches.Watch.Disposed).IsTrue();
    }

    [StaFact]
    public void AnUninstalledEnd_LeavesTheMenuHidden()
    {
        using var h = new Harness();
        h.Show();
        h.StartUninstall();

        h.Watches.Watch.Ended!.Invoke(UninstallHandoffEnd.Uninstalled, SetupOutcome.Uninstalled);

        Check.That(h.Coordinator.HandoffHiddenForTest).IsTrue();
        AssertHiddenClaims(h, "a completed uninstall");
        Check.That(h.Coordinator.WindowForTest!.IsVisible).IsFalse();
    }

    [StaFact]
    public void AManualReopen_CancelsTheWatch_AndTheLaterEndDoesNothing()
    {
        using var h = new Harness();
        h.Show();
        h.StartUninstall();
        var bandAtReopen = h.Cursor.Hides;
        Check.That(h.Watches.Watch.Disposed).IsFalse();

        // The owner's real entry: the toolbar/tray/keyboard send this message.
        h.Messenger.Send(new BackstageRequestedMessage());
        Check.That(StaTestPump.PumpUntil(
            () => !h.Coordinator.HandoffHiddenForTest, TimeSpan.FromSeconds(2))).IsTrue();

        Check.WithCustomMessage("the manual reopen did not cancel the watch")
            .That(h.Watches.Watch.Disposed).IsTrue();
        AssertOpenClaims(h, "the owner's reopen");
        Check.That(h.Cursor.Hides).IsEqualTo(bandAtReopen + 1);

        // The watch would have ended later; the cancelled one must not raise the window again.
        h.Watches.Watch.Ended!.Invoke(UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);
        h.Pump();

        AssertOpenClaims(h, "the ignored late end");
        Check.WithCustomMessage("the cancelled watch restored a second time")
            .That(h.Cursor.Hides).IsEqualTo(bandAtReopen + 1);
    }

    [StaFact]
    public void ClosingWhileHidden_CancelsTheWatch_AndTheLaterEndCannotShowAClosedWindow()
    {
        using var h = new Harness();
        h.Show();
        h.StartUninstall();
        Check.That(h.Coordinator.HandoffHiddenForTest).IsTrue();

        h.Coordinator.CloseBackstage();

        Check.That(h.Coordinator.IsOpen).IsFalse();
        Check.That(h.Coordinator.WindowForTest).IsNull();
        Check.That(h.Modal.BackstageOpen).IsFalse();
        Check.WithCustomMessage("closing during the hand-off did not cancel the watch")
            .That(h.Watches.Watch.Disposed).IsTrue();

        h.Watches.Watch.Ended!.Invoke(UninstallHandoffEnd.NotUninstalled, SetupOutcome.Cancelled);
        h.Pump();

        Check.That(h.Coordinator.IsOpen).IsFalse();
        Check.That(h.Coordinator.WindowForTest).IsNull();
        Check.That(h.Modal.BackstageOpen).IsFalse();
    }
}
