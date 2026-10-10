namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;

/// <summary>
/// Shared fakes and context builders for coordinator tests. Extracted from <c>EventCoordinatorTests</c>
/// so the decomposed sub-coordinator tests (Drag/QuickActions/...) can reuse the same fakes without
/// each test class re-declaring them.
/// </summary>
internal static class TestFakes
{
    /// <summary>
    /// Builds a context wired only enough to exercise input-simulator paths. <c>Messenger</c> is a
    /// real <see cref="WeakReferenceMessenger"/> because coordinators <c>Register</c> on it during
    /// <c>Initialize</c> and NRE on null.
    /// </summary>
    internal static EventCoordinationContext MakeContext(IInputSimulator input)
    {
        return new EventCoordinationContext(
            CursorVM: null!,
            ActivationVM: null!,
            ManualSelectionVM: null!,
            ManualSelectionIndicatorVM: null!,
            ActionMenuVM: null!,
            ToolbarVM: null!,
            KeyboardVM: null!,
            BackdropVM: null!,
            DragTrailVM: null!,
            OverlayManager: null!,
            GazeEngine: null!,
            InteractionManager: null!,
            Messenger: new WeakReferenceMessenger(),
            Dispatcher: null!,
            SettingsService: null!,
            Settings: null!,
            TargetHighlightVM: null!,
            InputSimulator: input,
            CalibrationService: null!,
            NoteManager: null!,
            TrackStatusVM: null!,
            TaskSwitcherVM: null!,
            WindowManager: null!,
            QuickActionsGridVM: null!,
            QuickActionsStore: null!,
            WindowsAppLauncher: null!,
            InstalledAppProvider: new FakeInstalledAppProvider(),
            ModesMenuVM: new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()),
            RepeatCursorVM: new RepeatCursorViewModel(new WeakReferenceMessenger()),
            EscapeHatch: new FakeEscapeHatch());
    }

    /// <summary>No-op hatch: App.Tests must never install a real system-wide keyboard hook.</summary>
    internal sealed class FakeEscapeHatch : IGlobalEscapeHatch
    {
        public event Action? EscapePressed;
        public bool IsActive => true;
        public void Dispose() { }
        /// <summary>Test seam: fires the event as the real hook would (on the caller's thread).</summary>
        public void Press() => EscapePressed?.Invoke();
    }

    /// <summary>
    /// Builds a context whose <see cref="EventCoordinationContext.WindowManager"/> is the given fake
    /// (mirrors how <see cref="MakeContext(IInputSimulator)"/> overrides the simulator). All other
    /// dependencies stay null! — only enough to exercise <c>ExecuteWindowAction</c>.
    /// </summary>
    internal static EventCoordinationContext MakeContextWithWindowManager(IWindowManager windowManager)
    {
        var ctx = MakeContext(new FakeInputSimulator());
        return ctx with { WindowManager = windowManager };
    }

    /// <summary>
    /// Builds a context wired enough for <see cref="Coordinators.DragCoordinator"/> to
    /// <c>Initialize</c>: its <c>OnInitialize</c> eagerly dereferences <c>InteractionManager</c>
    /// and <c>DragTrailVM</c>, which the default builder leaves <c>null!</c>. Both are real
    /// objects — neither has an App-layer dependency, and no mocking library is available.
    /// <c>OverlayManager</c> is left null here ON PURPOSE, so drag teardown hits a failure and the
    /// button-release assertions are observed through it — a release that only happens on the
    /// happy path is not the guarantee those tests need. Use
    /// <see cref="MakeDragContextWithBar"/> when the drag should SUCCEED; that supplies a fake bar,
    /// which became possible once <c>IOverlayManager.EdgeButtonBar</c> was narrowed to
    /// <see cref="IEdgeButtonBar"/>.
    /// </summary>
    internal static EventCoordinationContext MakeDragContext(IInputSimulator input)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        return MakeContext(input) with
        {
            Messenger = messenger,
            Settings = settings,
            InteractionManager = new InteractionManager(),
            DragTrailVM = new DragTrailViewModel(settings, messenger),
        };
    }

    /// <summary>
    /// Like <see cref="MakeDragContext"/> but with an overlay manager whose edge-button bar is a
    /// recording fake, so a drag can be started WITHOUT the setup failing.
    /// </summary>
    /// <remarks>
    /// This only became possible when <c>IOverlayManager.EdgeButtonBar</c> was narrowed from the
    /// concrete <c>EdgeButtonBarWindow</c> to <see cref="IEdgeButtonBar"/>. While it was the window,
    /// a fake had to return a real WPF window — pulling in an STA thread, an <c>Application</c>
    /// singleton and the app resource dictionary — so the drag success path had no headless
    /// coverage at all, and the mouse-button-left-pressed defect was invisible to the suite.
    /// </remarks>
    internal static (EventCoordinationContext Context, FakeEdgeButtonBar Bar) MakeDragContextWithBar(
        IInputSimulator input)
    {
        var bar = new FakeEdgeButtonBar();
        var ctx = MakeDragContext(input) with { OverlayManager = MakeOverlayManager(edgeButtonBar: bar) };
        return (ctx, bar);
    }

    /// <summary>
    /// Records what the app asked the edge bar to do, over a REAL <see cref="EdgeButtonBarViewModel"/>.
    ///
    /// <para>
    /// <see cref="EdgeBarVm"/> used to return <c>null</c>, which is not a neutral stub: production
    /// reads it to decide whether the bar currently belongs to the caller, and a null reference
    /// silently answers "no" to every such question. The one that matters is in
    /// <c>OverlayMessageCoordinator</c>'s <c>HideActionMenuMessage</c> handler —
    /// <c>bar?.EdgeBarVm?.DefinitionFor(ActionMenuEdgeButtons.CloseId) is not null</c> — the guard
    /// that stops a deferred menu-dismiss from clearing the drag's cancel button and scroll mode's
    /// stop button, i.e. the only way out of a drag for a user whose sole input channel is an eye
    /// tracker. With a null view model that condition was unreachable, so no test in this suite had
    /// ever executed the guard it protects, in either direction.
    /// </para>
    ///
    /// <para>
    /// Delegating to a real view model keeps <see cref="Calls"/> and <see cref="ShownButtonIds"/>
    /// exactly as they were while making <c>DefinitionFor</c> answer truthfully. The view model is
    /// pure data plus a <c>DwellDetector</c>; it needs no WPF, which is the same reason
    /// <see cref="MakeDragContextWithBar"/> became possible at all.
    /// </para>
    /// </summary>
    internal sealed class FakeEdgeButtonBar : IEdgeButtonBar
    {
        private readonly EdgeButtonBarViewModel _vm =
            new(new EyeCtrlSettings(), new WeakReferenceMessenger());

        public List<string> Calls { get; } = new();

        public List<string> ShownButtonIds { get; } = new();

        public EdgeButtonBarViewModel? EdgeBarVm => _vm;

        public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
        {
            Calls.Add("ShowButtons");
            ShownButtonIds.Clear();
            ShownButtonIds.AddRange(definitions.Select(d => d.Id));
            _vm.ShowButtons(definitions, edgeAnchor);
        }

        public void AddButton(EdgeButtonDefinition definition)
        {
            Calls.Add("AddButton:" + definition.Id);
            ShownButtonIds.Add(definition.Id);
            _vm.AddButton(definition);
        }

        public void RemoveButton(string id)
        {
            Calls.Add("RemoveButton:" + id);
            ShownButtonIds.Remove(id);
            _vm.RemoveButton(id);
        }

        public void Hide()
        {
            Calls.Add("Hide");
            ShownButtonIds.Clear();
            _vm.Hide();
        }
    }

    /// <summary>
    /// Builds a partial overlay manager: supply only the overlays a test needs. Every other member
    /// throws rather than returning null!, so a coordinator reaching beyond what the test intends
    /// fails loudly instead of with an NRE somewhere unrelated — the same discipline
    /// <c>ToolbarVisibilityAdapterTests.FakeOverlayManager</c> uses.
    /// </summary>
    internal static IOverlayManager MakeOverlayManager(
        IEdgeButtonBar? edgeButtonBar = null,
        IToolbarOverlay? toolbar = null,
        IPauseIndicatorOverlay? pauseIndicator = null,
        IManualSelectionOverlay? manualSelection = null,
        IManualSelectionIndicatorOverlay? manualSelectionIndicator = null,
        ICursorCircleOverlay? cursorCircle = null,
        IActivationButtonOverlay? activationButton = null,
        IGlassBackdropOverlay? glassBackdrop = null,
        IEdgeButtonBar? actionMenuModBar = null,
        IActionMenuOverlay? actionMenu = null,
        ICalibrationOverlay? calibration = null,
        ITrackStatusOverlay? trackStatus = null)
        => new PartialOverlayManager(
            edgeButtonBar, toolbar, pauseIndicator, manualSelection, manualSelectionIndicator,
            cursorCircle, activationButton, glassBackdrop, actionMenuModBar, actionMenu, calibration,
            trackStatus);

    /// <summary>No-op track-status overlay: SettingsUiCoordinator.OnInitialize start/stops its
    /// tracker, so W2/W3's initialised coordinator needs one present.</summary>
    internal sealed class FakeTrackStatusOverlay : ITrackStatusOverlay
    {
        public int StartTrackingCount { get; private set; }
        public int StopTrackingCount { get; private set; }
        public void Show() { }
        public void Hide() { }
        public void StartTracking() => StartTrackingCount++;
        public void StopTracking() => StopTrackingCount++;
        public void PositionTopRight() { }
    }

    /// <summary>No-op calibration overlay: the calibration state handler shows, updates and
    /// dismisses it on every state, so it must be present.</summary>
    internal sealed class FakeCalibrationOverlay : ICalibrationOverlay
    {
        public void Show() { }
        public void Hide() { }
        public void UpdateVisualState(CalibrationState state, double progress) { }
        public void DismissCalibration() { }
    }

    /// <summary>No-op action menu: the activation-show path hides it, so it must be present.</summary>
    internal sealed class FakeActionMenuOverlay : IActionMenuOverlay
    {
        public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
            int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
            PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
            int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false) { }

        public void Hide() { }
    }

    /// <summary>No-op cursor circle: records nothing, just satisfies the interface.</summary>
    internal sealed class FakeCursorCircleOverlay : ICursorCircleOverlay
    {
        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPausedDim(bool dimmed) { }
        public void SetPinned(bool pinned) { }
    }

    /// <summary>No-op activation button: records nothing, just satisfies the interface.</summary>
    internal sealed class FakeActivationButtonOverlay : IActivationButtonOverlay
    {
        public void Show() { }
        public void ShowAt(PhysicalPoint position, double fixationRadiusPixels) { }
        public void Hide() { }
        public void HideImmediate() { }
        public void UpdateDwellProgress(double progress) { }
    }

    /// <summary>Records escape-hint show/hide calls so a coordinator test can assert on them
    /// without constructing a real WPF window.</summary>
    internal sealed class FakeGlassBackdropOverlay : IGlassBackdropOverlay
    {
        public bool EscapeHintVisible { get; private set; }
        public List<string> Calls { get; } = new();

        public void Show() => Calls.Add("Show");

        // Mirrors GlassBackdropWindow.Hide(), which also collapses the hint so it never carries
        // over into an unrelated later use of the window.
        public void Hide() { EscapeHintVisible = false; Calls.Add("Hide"); }

        public void ApplyBackdrop(BackdropSettings settings) => Calls.Add("ApplyBackdrop");
        public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0) => Calls.Add("DrawHideButton");
        public void UpdateHideButtonProgress(double progress) => Calls.Add("UpdateHideButtonProgress");
        public void ShowEscapeHint() { EscapeHintVisible = true; Calls.Add("ShowEscapeHint"); }
        public void HideEscapeHint() { EscapeHintVisible = false; Calls.Add("HideEscapeHint"); }
    }

    /// <summary>No-op magnifier: satisfies coordinator tests that never let a handler reach it.</summary>
    internal sealed class FakeManualSelectionOverlay : IManualSelectionOverlay
    {
        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void Hide() { }
        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;

        // Integration stub. Throw rather than return default: (0,0) is a plausible-looking
        // coordinate, and this repo has been bitten by a real value that silently read as zero.
        public PhysicalPoint EffectiveCaptureCenter =>
            throw new NotImplementedException("this fake does not exercise the capture centre");
    }

    /// <summary>No-op gaze dot.</summary>
    internal sealed class FakeManualSelectionIndicatorOverlay : IManualSelectionIndicatorOverlay
    {
        public bool IsVisible => false;
        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(double screenX, double screenY) { }
        public void UpdateOuterDiameter(double diameter) { }
        public void AnimateReset(double targetDiameter, int durationMs) { }
        public void RaiseToTop() { }
        public void UpdateAnchor(double screenX, double screenY, bool visible) { }
    }

    private sealed class PartialOverlayManager : IOverlayManager
    {
        private readonly IEdgeButtonBar? _bar;
        private readonly IToolbarOverlay? _toolbar;
        private readonly IPauseIndicatorOverlay? _pauseIndicator;
        private readonly IManualSelectionOverlay? _manualSelection;
        private readonly IManualSelectionIndicatorOverlay? _manualSelectionIndicator;
        private readonly ICursorCircleOverlay? _cursorCircle;
        private readonly IActivationButtonOverlay? _activationButton;
        private readonly IGlassBackdropOverlay? _glassBackdrop;
        private readonly IEdgeButtonBar? _actionMenuModBar;
        private readonly IActionMenuOverlay? _actionMenu;
        private readonly ICalibrationOverlay? _calibration;
        private readonly ITrackStatusOverlay? _trackStatus;

        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();

        public PartialOverlayManager(IEdgeButtonBar? bar, IToolbarOverlay? toolbar,
            IPauseIndicatorOverlay? pauseIndicator, IManualSelectionOverlay? manualSelection,
            IManualSelectionIndicatorOverlay? manualSelectionIndicator,
            ICursorCircleOverlay? cursorCircle = null, IActivationButtonOverlay? activationButton = null,
            IGlassBackdropOverlay? glassBackdrop = null,
            IEdgeButtonBar? actionMenuModBar = null, IActionMenuOverlay? actionMenu = null,
            ICalibrationOverlay? calibration = null, ITrackStatusOverlay? trackStatus = null)
        {
            _bar = bar; _toolbar = toolbar; _pauseIndicator = pauseIndicator;
            _manualSelection = manualSelection; _manualSelectionIndicator = manualSelectionIndicator;
            _cursorCircle = cursorCircle; _activationButton = activationButton; _glassBackdrop = glassBackdrop;
            _actionMenuModBar = actionMenuModBar; _actionMenu = actionMenu; _calibration = calibration;
            _trackStatus = trackStatus;
        }

        public IEdgeButtonBar EdgeButtonBar =>
            _bar ?? throw new NotImplementedException("test supplied no edge-button bar");

        public IEdgeButtonBar ActionMenuModBar =>
            _actionMenuModBar ?? throw new NotImplementedException("test supplied no action-menu modifier bar");

        public IToolbarOverlay Toolbar =>
            _toolbar ?? throw new NotImplementedException("test supplied no toolbar");

        public IPauseIndicatorOverlay PauseIndicator =>
            _pauseIndicator ?? throw new NotImplementedException("test supplied no pause indicator");

        public ICursorCircleOverlay CursorCircle =>
            _cursorCircle ?? throw new NotImplementedException("test supplied no cursor circle");
        public IActivationButtonOverlay ActivationButton =>
            _activationButton ?? throw new NotImplementedException("test supplied no activation button");
        public IManualSelectionOverlay ManualSelectionOverlay =>
            _manualSelection ?? throw new NotImplementedException("test supplied no manual-selection overlay");
        public IActionMenuOverlay ActionMenu =>
            _actionMenu ?? throw new NotImplementedException("test supplied no action menu");
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator =>
            _manualSelectionIndicator ?? throw new NotImplementedException("test supplied no manual-selection indicator");
        public IGlassBackdropOverlay GlassBackdrop =>
            _glassBackdrop ?? throw new NotImplementedException("test supplied no glass backdrop");
        public ICalibrationOverlay Calibration =>
            _calibration ?? throw new NotImplementedException("test supplied no calibration overlay");
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus =>
            _trackStatus ?? throw new NotImplementedException("test supplied no track-status overlay");
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();
        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType) => throw new NotImplementedException();
        public void CloseAll() => throw new NotImplementedException();
    }

    /// <summary>
    /// Builds a context carrying the given settings — for tests that drive settings-driven setup
    /// (e.g. <see cref="Coordinators.QuickActionsCoordinator"/> suspend/resume).
    /// </summary>
    internal static EventCoordinationContext MakeContext(EyeCtrlSettings settings)
    {
        var ctx = MakeContext(new FakeInputSimulator());
        return ctx with { Settings = settings };
    }

    internal sealed class FakeInputSimulator : IInputSimulator
    {
        public ModifiedKeyPress? LastModifiedKeyPress { get; private set; }

        public (ushort Vk, bool Extended)? LastKeyPress { get; private set; }

        public PhysicalPoint? LastMiddleClick { get; private set; }

        /// <summary>
        /// Every button transition this fake was asked to perform, in order.
        /// <para>
        /// These were empty bodies. The recording fakes lived in <c>Core.Tests</c> — wired to
        /// <c>GazeEngine</c>, which only ever moves the cursor — while the layer that actually
        /// presses and releases the button discarded the calls. So the apparatus recorded injected
        /// input where none is injected and threw it away where it is, and <c>MouseUp</c> ended up
        /// asserted in none of the suite's ~2,900 tests.
        /// </para>
        /// <para>
        /// Only <c>MouseDown</c>/<c>MouseUp</c> are recorded here. The click methods —
        /// <c>LeftClick</c>, <c>MiddleClick</c> and friends — are single interface calls that press
        /// and release atomically, so they cannot leave a button held and do not belong in a ledger
        /// whose purpose is detecting one that is.
        /// </para>
        /// </summary>
        public List<(string Action, PhysicalPoint Point)> ButtonEvents { get; } = new();

        public int MouseDownCount => ButtonEvents.Count(e => e.Action == "MouseDown");

        public int MouseUpCount => ButtonEvents.Count(e => e.Action == "MouseUp");

        /// <summary>
        /// The invariant that matters: the OS button is not left pressed. Asserted as a count
        /// identity rather than per-path equality so it holds for exit paths nobody enumerated —
        /// which is how an unreleased button got in.
        /// </summary>
        public bool ButtonsBalanced => MouseDownCount == MouseUpCount;

        public List<(InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)> ModifiedClicks { get; } = new();
        public int BareClickCount { get; private set; }
        public List<(PhysicalPoint Point, int Dx, int Dy)> ModifiedScrolls { get; } = new();
        public List<(int Dx, int Dy)> BareScrolls { get; } = new();

        public void MoveCursor(PhysicalPoint point)
        {
        }

        public void LeftClick(PhysicalPoint point) => BareClickCount++;

        public void RightClick(PhysicalPoint point)
        {
        }

        public void DoubleClick(PhysicalPoint point)
        {
        }

        public void MiddleClick(PhysicalPoint point)
        {
            LastMiddleClick = point;
        }

        public void MouseDown(PhysicalPoint point) => ButtonEvents.Add(("MouseDown", point));

        public void MouseUp(PhysicalPoint point) => ButtonEvents.Add(("MouseUp", point));

        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) => BareScrolls.Add((deltaX, deltaY));

        // The MODIFIED overloads throw on null/empty (the dispatcher must never pass them a
        // nullable) and record otherwise:
        public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        {
            if (modifiers is not { Count: > 0 })
                throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
            ModifiedClicks.Add((InteractionAction.LeftClick, point, modifiers));
        }

        public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        {
            if (modifiers is not { Count: > 0 })
                throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
            ModifiedClicks.Add((InteractionAction.RightClick, point, modifiers));
        }

        public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        {
            if (modifiers is not { Count: > 0 })
                throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
            ModifiedClicks.Add((InteractionAction.MiddleClick, point, modifiers));
        }

        public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        {
            if (modifiers is not { Count: > 0 })
                throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
            ModifiedClicks.Add((InteractionAction.DoubleClick, point, modifiers));
        }

        public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
        {
            if (modifiers is not { Count: > 0 })
                throw new InvalidOperationException("dispatcher routed a bare scroll through the modified overload");
            ModifiedScrolls.Add((point, deltaX, deltaY)); // point recorded too (external review M3):
            // a dispatcher mutant passing the wrong point to the modified scroll survives without it
        }

        public void SendKeyPress(ushort virtualKeyCode)
        {
        }

        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)
        {
            LastKeyPress = (virtualKeyCode, isExtendedKey);
        }

        public void SendKeyDown(ushort virtualKeyCode)
        {
        }

        public void SendKeyUp(ushort virtualKeyCode)
        {
        }

        public void SendText(string text)
        {
        }

        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)
        {
            LastModifiedKeyPress = new ModifiedKeyPress(modifiers, virtualKeyCode, IsExtendedKey: false);
        }

        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
        {
            LastModifiedKeyPress = new ModifiedKeyPress(modifiers, virtualKeyCode, isExtendedKey);
        }
    }

    internal sealed record ModifiedKeyPress(IReadOnlySet<ModifierKey> Modifiers, ushort VirtualKeyCode, bool IsExtendedKey);

    internal sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }

    /// <summary>
    /// Superset of the three former private IGazeProvider fakes: settable <c>IsConnected</c> (defaults
    /// true, per the coordinator tests' connected-state precondition) plus a raisable GazePoint event.
    /// Not the Core test project's fake — that one's IsConnected has a private set flipped by Start().
    /// </summary>
    public class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; set; } = true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Raise(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    /// <summary>
    /// Fake <see cref="IWindowManager"/> that records the last op fired + how many times the
    /// eligibility query ran. EligibilityQueries is the proxy for "no deferred re-query" — a
    /// synchronous handler queries exactly once; a queued hop would (in production) re-query at
    /// pump time and (in a unit test) never pump at all.
    /// </summary>
    internal sealed class FakeWindowActionManager : IWindowManager
    {
        public IntPtr? EligibleHwnd { get; set; }
        public WindowAction? LastOp { get; private set; }
        public int EligibilityQueries { get; private set; }
        public bool ClosePosted { get; set; } = true;

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => Array.Empty<AppWindow>();
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow()
        {
            EligibilityQueries++;
            return EligibleHwnd;
        }
        public void Minimize(IntPtr hwnd) => LastOp = WindowAction.Minimize;
        public void MaximizeOrRestore(IntPtr hwnd) => LastOp = WindowAction.MaximizeRestore;
        public bool CloseWindow(IntPtr hwnd) { LastOp = WindowAction.Close; return ClosePosted; }
    }
}
