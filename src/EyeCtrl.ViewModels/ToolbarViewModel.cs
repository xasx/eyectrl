namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ToolbarViewModel : DwellOverlayViewModelBase,
    IRecipient<StateChangedMessage>,
    IRecipient<CalibrationCompletedMessage>,
    IRecipient<CalibrationCancelledMessage>,
    IRecipient<SelectionModeChangedMessage>
{
    private readonly Dictionary<string, ToolbarButtonViewModel> _buttonLookup = new();

    // Dwell-target parameters captured at construction: hit-test radius in physical pixels and
    // dwell time in ms. The targets themselves are created lazily (see the constructor note and
    // UpdateButtonScreenPosition), so these are held until the View reports a real position.
    private readonly double _buttonDwellRadiusPhysical;
    private readonly int _toolbarButtonDwellMs;

    // Buttons the View has already positioned. The first position report for a button creates its
    // dwell target; later reports only move it, so accumulated dwell state is preserved.
    private readonly HashSet<string> _positionedButtons = new();

    // Activity gate for dwell processing. Toggled by the auto-hide lifecycle (hide → false,
    // show/ForceShow → true) and calibration (Collecting/Training → false, terminal states → true)
    // via SetActive. When false, Receive short-circuits before feeding the detector, so a hidden or
    // calibrating toolbar cannot dwell from former screen positions (spec §4 BLOCKER C.2). Initial
    // state is true — the toolbar starts visible and dwellable.
    private bool _dwellingActive = true;

    [ObservableProperty] private bool _isPaused;

    private SelectionMode _currentSelectionMode = SelectionMode.None;

    public ObservableCollection<ToolbarButtonViewModel> Buttons { get; } = new();

    /// <summary>
    /// Screen positions of buttons, set by the View after layout.
    /// Keys are button IDs, values are screen-coordinate centers.
    /// </summary>
    public void UpdateButtonScreenPosition(string buttonId, PhysicalPoint screenCenter)
    {
        if (_positionedButtons.Add(buttonId))
        {
            // First real position for this button: create its dwell target now, at the true
            // on-screen center — never at the (0,0) placeholder. Because the View reports
            // positions only for buttons it actually renders, unrendered buttons never get a
            // target and so can't be dwell-activated from an off-screen/corner gaze.
            DwellDetector.AddTarget(new DwellTarget(buttonId, screenCenter,
                _buttonDwellRadiusPhysical, _toolbarButtonDwellMs));
        }
        else
        {
            // Later re-layout / re-dock: move the existing target without resetting accumulated
            // dwell state (preserves the fix that stopped dwell resetting on every frame).
            DwellDetector.UpdateTargetCenter(buttonId, screenCenter);
        }
    }

    public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink)
    {
        _buttonDwellRadiusPhysical = DpiHelper.LogicalToPhysicalX(settings.Toolbar.Size / 2);
        _toolbarButtonDwellMs = settings.Dwell.ToolbarButtonDwellMs;

        var buttons = new (string Id, string IconKey)[]
        {
            ("Pause", "Pause"),
            ("SelectionMode", SelectionModeIcons.None),
            ("Keyboard", "Keyboard"),
            // Calibrate is never rendered (ReachableElsewhere) and ToolbarIconKey has no crosshair
            // member of its own; this is a placeholder pending a real icon, not a considered choice.
            ("Calibrate", "Settings"),
            ("Settings", "Settings"),
            ("TaskSwitcher", "TaskSwitcher"),
            // QuickActions is never rendered (ReachableElsewhere) and ToolbarIconKey has no lightning-
            // bolt member of its own; this is a placeholder pending a real icon, not a considered choice.
            ("QuickActions", "Settings"),
            ("ResetAdaptation", "ResetAdaptation"),
        };

        foreach (var (id, iconKey) in buttons)
        {
            var btn = new ToolbarButtonViewModel(id, iconKey);
            Buttons.Add(btn);
            _buttonLookup[id] = btn;
            // Dwell targets are intentionally NOT created here. Each target is created lazily by
            // UpdateButtonScreenPosition once the View reports the button's real on-screen center.
            // This guarantees (a) only buttons the View actually renders ever become gaze targets,
            // and (b) no target ever sits at the (0,0) placeholder where a top-left-corner gaze
            // could dwell-activate an invisible button. Mirrors TaskSwitcherViewModel's deliberate
            // avoidance of the same "(0,0) race".
        }
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!_dwellingActive) return;
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    /// <summary>
    /// Activates/deactivates dwell processing for the toolbar. Called by the auto-hide
    /// lifecycle (hide → false, show/ForceShow → true) and calibration (Collecting/Training → false,
    /// Active/Failed/Cancelled/Idle → true). When deactivated, gaze samples are not fed to the detector
    /// and any in-progress dwell state is reset (preventing both hidden-toolbar fires and stale-
    /// timestamp instant activation on resume).
    ///
    /// Refcount invariant (spec §4): hide and calibration are mutually exclusive today — calibration
    /// only starts from controller-state Visible and Feed can't transition while calibration is active
    /// (ToolbarAutoHideController derives its gate from CalibrationService.IsSessionActive, NOT from
    /// CalibrationStateChangedMessage like this wiring). This flag is therefore a single bool, not a
    /// refcount. If that non-overlap invariant ever breaks, switch to a refcount here.
    ///
    /// Telemetry bias: ResetAllState fires TargetProgress(id,0) but NOT an Abandoned DwellOutcome,
    /// so a mid-flight dwell at hide/calibration-start drops out of struggle telemetry. Intentional —
    /// both numerator and denominator of ScoreAbandonment drop together; impact negligible.
    /// </summary>
    public void SetActive(bool active)
    {
        _dwellingActive = active;
        if (!active)
            DwellDetector.ResetAllState();
    }

    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.IconKey = IsPaused ? "Play" : "Pause";
    }

    public void Receive(CalibrationCompletedMessage message)
    {
        // Used to swap the Calibrate icon to a success or failure glyph depending on
        // message.Success. Calibrate is not on the toolbar (ToolbarButtonDriftTests.
        // ReachableElsewhere), so that swap was never visible to anyone; the icon swap returns
        // with the button. The empty body stays because the class declares
        // IRecipient<CalibrationCompletedMessage>, which requires this method to exist.
    }

    public void Receive(CalibrationCancelledMessage message)
    {
        // Used to reset the Calibrate icon back to its neutral crosshair — not a success/failure
        // swap, unlike CalibrationCompletedMessage above. Calibrate is not on the toolbar
        // (ToolbarButtonDriftTests.ReachableElsewhere), so that reset was never visible to anyone;
        // it returns with the button. The empty body stays because the class declares
        // IRecipient<CalibrationCancelledMessage>, which requires this method to exist.
    }

    public void Receive(SelectionModeChangedMessage message)
    {
        _currentSelectionMode = message.Mode;
        if (_buttonLookup.TryGetValue("SelectionMode", out var btn))
            btn.IconKey = SelectionModeIcons.ForMode(message.Mode);
    }

    /// <summary>For testing: simulate a button activation without gaze dwell.</summary>
    public void SimulateButtonActivated(string buttonId) => OnTargetActivated(buttonId);

    /// <summary>For testing: simulate dwell progress on a target without gaze.</summary>
    /// <param name="buttonId">The button id.</param>
    /// <param name="progress">Progress in 0..1.</param>
    public void SimulateTargetProgress(string buttonId, double progress)
        => OnTargetProgress(buttonId, progress);

    /// <summary>Raised after a button's dwell completes, for view-side activation feedback.</summary>
    public event Action<string>? ButtonActivated;   // Task 8 raises this

    protected override void OnTargetActivated(string targetId)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = 0;

        switch (targetId)
        {
            case "Pause": Messenger.Send(new PauseToggledMessage()); break;
            case "SelectionMode":
                var next = (SelectionMode)(((int)_currentSelectionMode + 1) % Enum.GetValues<SelectionMode>().Length);
                Messenger.Send(new SelectionModeChangedMessage(next));
                break;
            case "Keyboard": Messenger.Send(new KeyboardToggledMessage()); break;
            case "Calibrate": Messenger.Send(new CalibrationRequestedMessage()); break;
            case "Settings": Messenger.Send(new SettingsRequestedMessage()); break;
            case "TaskSwitcher": Messenger.Send(new TaskSwitcherToggledMessage()); break;
            case "QuickActions": Messenger.Send(new QuickActionsToggledMessage()); break;
            case "ResetAdaptation": Messenger.Send(new ResetAdaptationMessage()); break;
        }
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = progress;
    }
}
