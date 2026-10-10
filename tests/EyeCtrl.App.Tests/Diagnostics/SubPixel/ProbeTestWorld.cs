namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// The headless world W1/W3/W7/W8/W9 drive: a real <see cref="GazeEngine"/> (so suppression claims
/// are observable on the engine itself), the recording <see cref="RecordingProbeHost"/>, a fake
/// clock and the fake watchdog-timer factory. Continuations and <c>Check</c> are invoked directly by
/// the tests, so nothing here depends on real time or a desktop.
///
/// <para>
/// Every case in W1 starts with <c>FixationSuppressor.Keyboard</c> held on the engine when asked
/// for, because review 05 F2's end state is "the keyboard was never touched": releasing the keyboard
/// claim at the end of a case turns "was the probe's own claim released?" into a single
/// <c>SuppressFixation</c> read.
/// </para>
/// </summary>
internal sealed class ProbeTestWorld : IDisposable
{
    internal readonly ModalInteractionState Modal = new();
    internal readonly EyeCtrlSettings Settings = new();
    internal readonly WeakReferenceMessenger Messenger = new();
    internal readonly Dispatcher Dispatcher = Dispatcher.CurrentDispatcher;
    internal readonly TestFakes.FakeGazeProvider Provider = new();
    internal readonly GazeEngine Engine;
    internal readonly CursorCircleViewModel CursorVm;
    internal readonly RecordingProbeHost Host;
    internal readonly FakeProbeTimerFactory Timers = new();
    internal readonly SubPixelProbeCoordinator Coordinator;
    internal readonly EventCoordinationContext Context;
    internal readonly List<SubPixelProbeViewModel> Vms = new();
    internal readonly string ResultsDir = TempTestDirectory.Create("subpixel-probe");

    /// <summary>W2/W3's real summon owner; the host forwards suspend/resume into it.</summary>
    internal QuickActionsCoordinator? QuickActions { get; }

    /// <summary>The real summon controller W2/W3 inject, so <c>IsSummonSuspended</c> is not vacuous.</summary>
    internal QuickActionsSummonController? Summon { get; }

    /// <summary>The fully initialised settings coordinator W2/W3 drive.</summary>
    internal SettingsUiCoordinator? SettingsUi { get; }

    internal long Now = 1_000_000;

    internal bool ValidSample = true;

    internal ProbeTestWorld(
        bool holdKeyboardClaim = false,
        bool withQuickActions = false,
        bool withSettingsUi = false)
    {
        Engine = new GazeEngine(Provider, new TestFakes.FakeInputSimulator(), Settings);
        if (holdKeyboardClaim) Engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);

        CursorVm = new CursorCircleViewModel(Settings, Messenger);
        CursorVm.IsVisible = true;

        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            GazeEngine = Engine,
            InteractionManager = Engine.InteractionManager,
            Settings = Settings,
            Messenger = Messenger,
            Dispatcher = Dispatcher,
            CursorVM = CursorVm,
            TaskSwitcherVM = new TaskSwitcherViewModel(new TestFakes.FakeWindowActionManager(), Settings, Messenger),
            QuickActionsGridVM = new QuickActionsGridViewModel(new NoOpQuickActionsStore(), Settings, Messenger),
            OverlayManager = TestFakes.MakeOverlayManager(
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                trackStatus: new TestFakes.FakeTrackStatusOverlay()),
            CalibrationService = new EyeCtrl.Core.Calibration.CalibrationService(),
        };
        Context = context;

        if (withQuickActions)
        {
            QuickActions = new QuickActionsCoordinator(Modal);
            Summon = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
            QuickActions.InjectSummonForTesting(Summon);
            QuickActions.Initialize(context);
            QuickActions.SetSummonSuspended(false);
            Check.That(QuickActions.IsSummonSuspended).IsFalse();
        }

        if (withSettingsUi)
        {
            var toolbar = new ToolbarLifecycleCoordinator(Modal);
            SettingsUi = new SettingsUiCoordinator(
                Modal, QuickActions!, toolbar,
                new KeyboardCoordinator(Modal, QuickActions!, toolbar));
            SettingsUi.Initialize(context);
        }

        Host = new RecordingProbeHost(Modal, Dispatcher, Engine, () => CursorVm.IsVisible)
        {
            ViewModelFactory = (schedule, timeProvider, isValidSample, startMode, verdict) =>
            {
                var vm = new SubPixelProbeViewModel(
                    Messenger, timeProvider, schedule, Settings.Dwell, isValidSample, startMode, verdict);
                Vms.Add(vm);
                return vm;
            },
            SummonSink = withQuickActions
                ? suspended => QuickActions!.SetSummonSuspended(suspended)
                : null,
        };

        Coordinator = new SubPixelProbeCoordinator(Modal, Host, () => Now, Timers.Create, ResultsDir);
        Coordinator.Initialize(context);
    }

    /// <summary>The live session; only valid while a run is up.</summary>
    internal ProbeSession Session => Coordinator.LiveSessionForTest
        ?? throw new InvalidOperationException("No live probe session.");

    /// <summary>Drains everything queued, including the ContextIdle <c>CaptureAndShow</c>.</summary>
    internal void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Starts a run and drives it to the shown state.</summary>
    internal ProbeSession StartShown()
    {
        Coordinator.StartForTest();
        Pump();
        return Session;
    }

    /// <summary>Releases the keyboard claim the world seeded, turning "no probe claim" into one read.</summary>
    internal void ReleaseSeededKeyboardClaim() =>
        Engine.ReleaseFixationSuppression(FixationSuppressor.Keyboard);

    public void Dispose()
    {
        Coordinator.Dispose();
        SettingsUi?.Dispose();
        QuickActions?.Dispose();
        Engine.Dispose();
    }
}

/// <summary>An empty quick-action store for the grid VM the coordinator's menu check reads.</summary>
internal sealed class NoOpQuickActionsStore : IQuickActionsStore
{
    public IReadOnlyList<QuickAction> LoadAll() => [];
    public void Save(IReadOnlyList<QuickAction> actions) { }
    public QuickAction? FindById(string id) => null;
}
