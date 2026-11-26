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
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The Backstage coordinator's screen ownership (Ticket 03): refusal while another screen-owning
/// surface is up, the open/close claim order, the deterministic unwind, and the Calibrate
/// close-before-send ordering. The window-level presentation lives in
/// <see cref="BackstageWindowTests"/>.
/// </summary>
[Collection("WpfApplication")]
public class BackstageUiCoordinatorTests
{
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
        public TestFakes.FakeGlassBackdropOverlay Backdrop { get; } = new();
        public CursorCircleViewModel CursorVm { get; }
        public ToolbarViewModel ToolbarVm { get; }
        public KeyboardViewModel KeyboardVm { get; }
        public ModesMenuViewModel ModesVm { get; }
        public TaskSwitcherViewModel TaskSwitcherVm { get; }
        public QuickActionsGridViewModel QuickActionsVm { get; }
        public ActionMenuViewModel ActionMenuVm { get; }
        public BackstageViewModel BackstageVm { get; }
        public BackstageUiCoordinator Coordinator { get; }
        public List<string> Shutdowns { get; } = new();
        public bool ExerciseActive { get; set; }

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
                glassBackdrop: Backdrop);

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

            Coordinator = new BackstageUiCoordinator(
                Modal,
                new QuickActionsCoordinator(Modal),
                new ToolbarLifecycleCoordinator(Modal),
                () => ExerciseActive);
            Coordinator.CreateWindowForTest = static () =>
                InvisibleTestWindow.Prepare(new BackstageWindow());
            Coordinator.SetShutdown(() => Shutdowns.Add("shutdown"));
            Coordinator.Initialize(context);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            BackstageVm.Dispose();
            ToolbarVm.Dispose();
            KeyboardVm.Dispose();
            ActionMenuVm.Dispose();
            CursorVm.Dispose();
        }
    }

    [StaTheory]
    [InlineData("probe")]
    [InlineData("guide")]
    [InlineData("exercise")]
    public void Open_IsRefused_WhileAnotherScreenOwningSurfaceIsUp(string blocker)
    {
        using var h = new Harness();
        switch (blocker)
        {
            case "probe": h.Modal.ProbeOpen = true; break;
            case "guide": h.Modal.GuideOpen = true; break;
            default: h.ExerciseActive = true; break;
        }

        h.Coordinator.ShowBackstage();

        Check.That(h.Coordinator.IsOpen).IsFalse();
        Check.That(h.Modal.BackstageOpen).IsFalse();
        Check.That(h.Cursor.Hides).IsEqualTo(0);
    }

    [StaFact]
    public void Open_ClaimsTheScreen_AndDismissesTheTransientPeers()
    {
        using var h = new Harness();
        h.KeyboardVm.IsVisible = true;
        h.ModesVm.IsVisible = true;
        h.TaskSwitcherVm.IsVisible = true;
        h.QuickActionsVm.IsVisible = true;
        h.ActionMenuVm.IsVisible = true;
        h.Backdrop.Show();

        h.Coordinator.ShowBackstage();

        Check.That(h.Coordinator.IsOpen).IsTrue();
        Check.That(h.Modal.BackstageOpen).IsTrue();
        Check.That(h.Cursor.Hides).IsEqualTo(1);
        Check.That(h.KeyboardVm.IsVisible).IsFalse();
        Check.That(h.ModesVm.IsVisible).IsFalse();
        Check.That(h.TaskSwitcherVm.IsVisible).IsFalse();
        Check.That(h.QuickActionsVm.IsVisible).IsFalse();
        Check.That(h.ActionMenuVm.IsVisible).IsFalse();
        Check.That(h.Backdrop.Calls).Contains("Hide");
        Check.That(h.Backdrop.Calls).Contains("CompleteHideNow");
    }

    [StaFact]
    public void Close_ReleasesEveryClaim_IncludingTheBackstageOpenFlagAndTheRingGuard()
    {
        using var h = new Harness();
        h.Coordinator.ShowBackstage();
        Check.That(h.Modal.BackstageOpen).IsTrue();

        h.Modal.SettingsOpen = true;      // another modal still owns the screen
        h.Coordinator.CloseBackstage();

        Check.That(h.Coordinator.IsOpen).IsFalse();
        Check.That(h.Modal.BackstageOpen).IsFalse();
        Check.That(h.Cursor.Shows).IsEqualTo(0);   // the ring stays hidden under Settings

        h.Modal.SettingsOpen = false;
        h.Coordinator.ShowBackstage();
        h.Coordinator.CloseBackstage();
        Check.That(h.Cursor.Shows).IsEqualTo(1);
    }

    [StaFact]
    public void Calibrate_ClosesBackstageBeforeSendingTheRequest()
    {
        using var h = new Harness();
        h.Coordinator.ShowBackstage();

        var openAtSend = true;
        var backstageFlagAtSend = true;
        h.Messenger.Register<CalibrationRequestedMessage>(this, (_, _) =>
        {
            openAtSend = h.Coordinator.IsOpen;
            backstageFlagAtSend = h.Modal.BackstageOpen;
        });

        h.BackstageVm.ActivateForMouse(BackstageViewModel.CalibrateCardId);

        Check.That(openAtSend).IsFalse();
        Check.That(backstageFlagAtSend).IsFalse();
    }

    [StaFact]
    public void Escape_DismissesAnOpenModalFirst_ThenClosesTheMenu()
    {
        using var h = new Harness();
        h.Coordinator.ShowBackstage();
        h.BackstageVm.ActivateForMouse(BackstageViewModel.QuitCardId);
        Check.That(h.BackstageVm.IsModalOpen).IsTrue();

        h.Escape.Press();
        Check.That(StaTestPump.PumpUntil(() => !h.BackstageVm.IsModalOpen, TimeSpan.FromSeconds(2))).IsTrue();
        Check.That(h.Coordinator.IsOpen).IsTrue();

        h.Escape.Press();
        Check.That(StaTestPump.PumpUntil(() => !h.Coordinator.IsOpen, TimeSpan.FromSeconds(2))).IsTrue();
    }

    [StaFact]
    public void SetupGateways_AreFreshPerOpen()
    {
        using var h = new Harness();
        var created = 0;
        h.Coordinator.SetSetupGateways(() =>
        {
            created++;
            return new FakeGateways();
        });

        h.Coordinator.ShowBackstage();
        Check.That(created).IsEqualTo(1);
        h.Coordinator.CloseBackstage();

        h.Coordinator.ShowBackstage();
        Check.That(created).IsEqualTo(2);
    }

    private sealed class FakeGateways : ISetupGateways
    {
        public bool Installed => false;
        public bool PreviousAvailable => false;
        public string? PreviousVersion => null;
        public bool PolicyBlocks => false;
        public SetupUpdateOffer? UpdateOffer => null;
        public bool UpdateOfferPending => false;
        public void BeginUpdateOfferProbe(Action whenDone) => whenDone();
        public SetupHandoffStart StartUpdateHandoff(SetupUpdateOffer offer) => SetupHandoffStart.Failed;
        public SetupHandoffStart StartRollbackHandoff() => SetupHandoffStart.Failed;
        public SetupHandoffStart StartUninstallHandoff() => SetupHandoffStart.Failed;
    }

    [StaFact]
    public void PresencePoll_RefreshesTheClock_AndTheOneRefresherStopsOnClose()
    {
        using var h = new Harness();
        h.Coordinator.ShowBackstage();
        Check.That(h.Coordinator.PresenceRefresherRunningForTest).IsTrue();

        var window = h.Coordinator.WindowForTest!;
        var fixedTime = new DateTime(2026, 11, 25, 14, 32, 0);
        window.ClockNow = () => fixedTime;
        h.Coordinator.TickPresenceForTest();
        Check.That(window.ClockTimeForTest).IsEqualTo("14:32");

        h.Coordinator.CloseBackstage();
        Check.That(h.Coordinator.PresenceRefresherRunningForTest).IsFalse();
        var afterClose = window.ClockTimeForTest;
        window.ClockNow = () => fixedTime.AddMinutes(5);
        h.Coordinator.TickPresenceForTest();
        Check.That(window.ClockTimeForTest).IsEqualTo(afterClose);
    }

    [StaFact]
    public void BackstageRequestedMessage_OpensTheMenu_AndCloseMessage_ClosesIt()
    {
        using var h = new Harness();

        h.Messenger.Send(new BackstageRequestedMessage());
        Check.WithCustomMessage("BackstageRequestedMessage no longer reaches the coordinator's open path.")
            .That(StaTestPump.PumpUntil(() => h.Coordinator.IsOpen, TimeSpan.FromSeconds(2))).IsTrue();

        h.Messenger.Send(new CloseBackstageMessage());
        Check.WithCustomMessage("CloseBackstageMessage no longer reaches the coordinator's close path.")
            .That(StaTestPump.PumpUntil(() => !h.Coordinator.IsOpen, TimeSpan.FromSeconds(2))).IsTrue();
    }

    [StaFact]
    public void QuitConfirm_InvokesTheInjectedShutdown()
    {
        using var h = new Harness();
        h.Coordinator.ShowBackstage();

        h.BackstageVm.ActivateForMouse(BackstageViewModel.QuitCardId);
        h.BackstageVm.ActivateForMouse(BackstageWindow.QuitConfirmChoiceId);

        Check.That(h.Shutdowns).ContainsExactly("shutdown");
    }

    [StaFact]
    public void Quit_ShutdownSeesSupervisorRelaunchRequestedFalse()
    {
        // The ticket's acceptance criterion: Quit must leave the supervisor on standby. The real
        // AppRestartCoordinator's flag is read at the shutdown callback; Quit must never have set
        // it (only a restart does).
        using var h = new Harness();
        var restart = new AppRestartCoordinator(
            h.Messenger,
            h.Settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => { },
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => false,
            bringUp: NoBringUp(h.Settings));
        h.Coordinator.SetRestartCoordinator(restart);

        bool? atShutdown = null;
        h.Coordinator.SetShutdown(() => atShutdown = restart.SupervisorRelaunchRequested);
        h.Coordinator.ShowBackstage();
        h.BackstageVm.ActivateForMouse(BackstageViewModel.QuitCardId);
        h.BackstageVm.ActivateForMouse(BackstageWindow.QuitConfirmChoiceId);

        Check.That(atShutdown).IsNotNull();
        Check.That(atShutdown!.Value).IsFalse();
    }

    private static SupervisorBringUp NoBringUp(EyeCtrlSettings settings) =>
        new(settings,
            enablePublishing: () => { },
            launchSupervisor: () => false,
            persistEnabled: _ => { },
            reconcileRunKey: () => { },
            tryReachSupervisor: () => false);

    [StaFact]
    public void AnUnexpectedWindowClose_ReleasesTheClaims()
    {
        // Alt+F4 / an OS close: the window has already run its own OnClosed, so the coordinator
        // must release the screen claims without touching the closed window again.
        using var h = new Harness();
        h.Coordinator.ShowBackstage();
        Check.That(h.Coordinator.IsOpen).IsTrue();

        h.Coordinator.WindowForTest!.Close();

        Check.That(StaTestPump.PumpUntil(() => !h.Coordinator.IsOpen, TimeSpan.FromSeconds(2))).IsTrue();
        Check.That(h.Modal.BackstageOpen).IsFalse();
        Check.That(h.Cursor.Shows).IsEqualTo(1);
    }

    [StaFact]
    public void AnOpenThatThrows_UnwindsEveryClaim()
    {
        using var h = new Harness();
        h.Coordinator.CreateWindowForTest = static () => throw new InvalidOperationException("window construction failed");

        h.Coordinator.ShowBackstage();

        Check.That(h.Coordinator.IsOpen).IsFalse();
        Check.That(h.Modal.BackstageOpen).IsFalse();
        Check.That(h.Cursor.Shows).IsEqualTo(1);
    }
}
