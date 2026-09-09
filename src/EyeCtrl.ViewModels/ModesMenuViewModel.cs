namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The modes menu (rework D1′/D18): a small panel under the toolbar, toggled by the toolbar's
/// Modes button, holding the input-mode switches — currently one row, "Repeat clicks". A row
/// dwell toggles its mode; the menu hides on selection. Follows the TaskSwitcher pattern: the
/// VM receives the toggle message itself and flips <see cref="DwellOverlayViewModelBase"/>
/// IsVisible; the binding coordinator shows/hides the window; the window reports the row's real
/// screen position back (no dwell target exists before that — the toolbar's (0,0) lesson).
///
/// <para>
/// Known shared behaviour, deliberately inherited: like the Task Switcher grid, this panel does
/// not suppress engine fixation, so resting on a row past the fixation window can raise the
/// activation button underneath. Same exposure class as the existing surfaces; revisit only if
/// it bothers in practice.
/// </para>
/// </summary>
public partial class ModesMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ModesMenuToggledMessage>,
    IRecipient<StateChangedMessage>
{
    private const string RepeatRowId = "modes_repeat";

    /// <summary>Dwell for a mode row: same as toolbar buttons — this is toolbar-adjacent chrome.</summary>
    private readonly int _rowDwellMs;

    private bool _repeatActive;

    /// <summary>State word shown beside the row label ("on"/"off").</summary>
    [ObservableProperty] private string _repeatRowStateText = "off";

    /// <summary>0..1 dwell progress of the row, for the fill bar.</summary>
    [ObservableProperty] private double _rowDwellProgress;

    public ModesMenuViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink, dwellRegistry)
    {
        _rowDwellMs = settings.Dwell.ToolbarButtonDwellMs;
    }

    public void Receive(ModesMenuToggledMessage message)
    {
        if (IsVisible)
            HideOverlay();
        else
            IsVisible = true;   // the binding coordinator shows the window; it reports the row position
    }

    public void Receive(StateChangedMessage message)
    {
        _repeatActive = message.NewState == InteractionState.RepeatMode;
        RepeatRowStateText = _repeatActive ? "on" : "off";
    }

    /// <summary>
    /// Window feedback after placement: the row's real screen centre and hit radius. Until this
    /// arrives the VM holds no dwell target — a target at a placeholder point could fire on a
    /// corner gaze (the ToolbarWindow lesson, mirrored deliberately).
    /// </summary>
    public void SetRowScreenPosition(PhysicalPoint center, double radiusPhysical)
    {
        DwellDetector.ClearTargets();
        DwellDetector.AddTarget(new DwellTarget(RepeatRowId, center, radiusPhysical, _rowDwellMs));
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId != RepeatRowId) return;
        if (_repeatActive)
            Messenger.Send(new ExitRepeatModeMessage());
        else
            Messenger.Send(new EnterRepeatModeMessage());
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == RepeatRowId)
            RowDwellProgress = progress;
    }

    public void HideOverlay()
    {
        IsVisible = false;
        RowDwellProgress = 0;
        DwellDetector.ClearTargets();
    }

    /// <summary>Drives one activation without a DwellDetector, for tests.</summary>
    internal void ActivateForTest(string targetId) => OnTargetActivated(targetId);
}
