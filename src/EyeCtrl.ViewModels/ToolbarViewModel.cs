namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ToolbarViewModel : DwellOverlayViewModelBase,
    IRecipient<StateChangedMessage>,
    IRecipient<RepeatActionChangedMessage>,
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

    /// <summary>The Pause toggle's target id, used by the leave-before-re-arm gate.</summary>
    private const string PauseTargetId = "Pause";

    // True from a Pause activation until gaze has LEFT the Pause target for long enough to be a
    // real leave. While set, the toggle cannot re-fire and the detector is not fed on the target,
    // so one held look produces exactly one toggle instead of one per cooldown cycle (2026-09-24:
    // a 3.5 s hold toggled 3 times).
    private bool _pauseReArmPending;

    // Start of the current continuous off-target run while the latch is pending. Reset whenever a
    // sample is inside the target. A single stray sample outside the circle must not re-arm a
    // toggle the user is still looking at.
    private long? _pauseOffTargetSinceUs;

    /// <summary>
    /// How long gaze must be continuously off the Pause target before the re-arm latch clears.
    /// About four tracker frames at the Tobii cadence (~33 ms): long enough to reject a single
    /// outlier, short enough that a deliberate look away re-arms within a glance.
    /// </summary>
    private const long PauseReArmLeaveUs = 120 * MathConstants.MicrosecondsPerMillisecond;

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

    public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink, dwellRegistry)
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
            ("Modes", "Modes"),
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
        // Gap-held samples carry the frozen last valid position, so a held point must not clear the
        // re-arm latch either — that is the same reason they are skipped, not fed, below.
        if (!message.IsInGap)
            UpdatePauseReArm(message.Position, message.TimestampMicroseconds);

        // Gap-held samples carry the frozen last valid position, so they are skipped rather than fed
        // (see DwellOverlayViewModelBase.Receive) — a blink over a toolbar button must not complete it.
        // The pause term routes through the shared gate (round-2 finding 2): this override used to
        // reimplement the checks, silently bypassing DwellActiveWhilePaused below and leaving the
        // toolbar's unpause opt-in as dead code.
        if (!_dwellingActive || !SampleMayDwell(message)) return;

        // Leave-before-re-arm for the pause toggle: while gaze still rests on the toggle that just
        // fired, do not feed the detector at all. Without this, the shared cooldown alone re-arms at
        // ~1.1 s and a single 3.5 s look toggles the app three times.
        if (_pauseReArmPending && IsOverPauseTarget(message.Position)) return;

        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    /// <summary>
    /// Clears the pause toggle's re-arm latch once gaze has been continuously off its target for
    /// <see cref="PauseReArmLeaveUs"/>. Frame-gated by the caller and deliberately independent of
    /// <c>_dwellingActive</c>: leaving is leaving whether or not the detector is currently fed.
    /// </summary>
    private void UpdatePauseReArm(PhysicalPoint position, long timestampMicroseconds)
    {
        if (!_pauseReArmPending) return;

        if (IsOverPauseTarget(position))
        {
            _pauseOffTargetSinceUs = null; // back inside: any off-target run so far was not a leave
            return;
        }

        _pauseOffTargetSinceUs ??= timestampMicroseconds;
        if (timestampMicroseconds - _pauseOffTargetSinceUs.Value >= PauseReArmLeaveUs)
        {
            _pauseReArmPending = false;
            _pauseOffTargetSinceUs = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="position"/> is inside the Pause toggle's LIVE hit area. The detector
    /// is asked rather than the ViewModel recomputing from the as-registered radius: adaptation's
    /// geometry routing may have grown the target, and a latch that clears inside the grown hit
    /// area is the same re-arm storm this guard exists to stop. False before the target exists —
    /// with no target there is nothing to leave, so a never-positioned toggle cannot latch forever.
    /// </summary>
    private bool IsOverPauseTarget(PhysicalPoint position) =>
        DwellDetector.HitTest(PauseTargetId, position);

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

    private InteractionAction? _repeatAction;

    public void Receive(RepeatActionChangedMessage message)
    {
        _repeatAction = message.Action;
        if (_buttonLookup.TryGetValue("Modes", out var btn))
            btn.IconKey = message.Action is { } a ? ModesIcons.ForAction(a) : ModesIcons.Off;
    }

    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.IconKey = IsPaused ? "Play" : "Pause";
        if (message.NewState != InteractionState.RepeatMode && _repeatAction is not null)
        {
            // Leaving the mode also resets the Modes button — the action message covers entry,
            // this covers every exit path (stop button, ESC, auto-exit) uniformly.
            _repeatAction = null;
            if (_buttonLookup.TryGetValue("Modes", out var modesBtn))
                modesBtn.IconKey = ModesIcons.Off;
        }
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

    /// <summary>Raised after a button's dwell completes, for view-side activation feedback
    /// (e.g. a success-coloured flash). Task 8 owns the View's response; this class only raises
    /// it, from <see cref="OnTargetActivated"/>.</summary>
    public event Action<string>? ButtonActivated;

    /// <summary>For testing: simulate a button activation without gaze dwell.</summary>
    public void SimulateButtonActivated(string buttonId) => OnTargetActivated(buttonId);

    /// <summary>For testing: simulate dwell progress on a target without gaze.</summary>
    /// <param name="buttonId">The button id.</param>
    /// <param name="progress">Progress in 0..1.</param>
    public void SimulateTargetProgress(string buttonId, double progress)
        => OnTargetProgress(buttonId, progress);

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the toolbar band is the gaze-reachable unpause affordance (pause deliberately does not gate toolbar dwell).
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
        {
            btn.DwellProgress = 0;
            btn.IsHighlighted = false;
        }

        switch (targetId)
        {
            case "Pause":
                // Latched BEFORE the toggle is sent: from this instant the user's gaze is on the
                // toggle, and a re-fire (after the cooldown) must wait until it leaves for good.
                // The literal (not PauseTargetId) keeps ToolbarButtonDriftTests' source scan able
                // to see this button has an activation case.
                _pauseReArmPending = true;
                _pauseOffTargetSinceUs = null;
                Messenger.Send(new PauseToggledMessage());
                break;
            case "SelectionMode":
                var next = (SelectionMode)(((int)_currentSelectionMode + 1) % Enum.GetValues<SelectionMode>().Length);
                Messenger.Send(new SelectionModeChangedMessage(next));
                break;
            case "Keyboard": Messenger.Send(new KeyboardToggledMessage()); break;
            case "Calibrate": Messenger.Send(new CalibrationRequestedMessage()); break;
            case "Settings": Messenger.Send(new SettingsRequestedMessage()); break;
            case "TaskSwitcher": Messenger.Send(new TaskSwitcherToggledMessage()); break;
            case "Modes": Messenger.Send(new ModesMenuToggledMessage()); break;
            case "QuickActions": Messenger.Send(new QuickActionsToggledMessage()); break;
            case "ResetAdaptation": Messenger.Send(new ResetAdaptationMessage()); break;
        }

        ButtonActivated?.Invoke(targetId);
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (!_buttonLookup.TryGetValue(targetId, out var btn))
            return;

        btn.DwellProgress = progress;
        // Derived rather than separately plumbed. DwellDetector fires no progress for the FIRST
        // sample inside a target (DwellDetector.cs:224-228) and a non-zero value from the second
        // on (lines 229-233); it fires exactly 0 on leave (line 253) and on activation (line 276).
        // So "progress > 0" is "gaze is on this target", one gaze sample after entry — ~30 ms at
        // the tracker's ~33 Hz (TobiiStreamEngineProvider.cs:195).
        btn.IsHighlighted = progress > 0;
    }
}
