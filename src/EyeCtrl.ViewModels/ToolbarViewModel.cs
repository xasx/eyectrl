namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
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

    [ObservableProperty] private bool _isPaused;

    private SelectionMode _currentSelectionMode = SelectionMode.None;

    public ObservableCollection<ToolbarButtonViewModel> Buttons { get; } = new();

    /// <summary>
    /// Screen positions of buttons, set by the View after layout.
    /// Keys are button IDs, values are screen-coordinate centers.
    /// </summary>
    public void UpdateButtonScreenPosition(string buttonId, PhysicalPoint screenCenter)
    {
        DwellDetector.UpdateTargetCenter(buttonId, screenCenter);
    }

    public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "\u23F8"),
            ("SelectionMode", "\u2295"),
            ("Keyboard", "\u2328"),
            ("Calibrate", "\u2316"),
            ("Settings", "\u2699"),
            ("TaskSwitcher", "\uD83E\uDE9F"),  // 🪟 (surrogate pair, same as PinNote's non-BMP pattern)
        };

        foreach (var (id, symbol) in buttons)
        {
            var btn = new ToolbarButtonViewModel(id, symbol);
            Buttons.Add(btn);
            _buttonLookup[id] = btn;
            DwellDetector.AddTarget(new DwellTarget(id,
                new PhysicalPoint(0, 0),
                DpiHelper.LogicalToPhysicalX(settings.Toolbar.Size / 2),
                settings.Dwell.ToolbarButtonDwellMs));
        }
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.Symbol = IsPaused ? "\u25B6" : "\u23F8";
    }

    public void Receive(CalibrationCompletedMessage message)
    {
        if (_buttonLookup.TryGetValue("Calibrate", out var btn))
            btn.Symbol = message.Success ? "\u2713" : "\u26A0";
    }

    public void Receive(CalibrationCancelledMessage message)
    {
        if (_buttonLookup.TryGetValue("Calibrate", out var btn))
            btn.Symbol = "\u2316";
    }

    public void Receive(SelectionModeChangedMessage message)
    {
        _currentSelectionMode = message.Mode;
        if (_buttonLookup.TryGetValue("SelectionMode", out var btn))
            btn.Symbol = SelectionModeGlyphs.ForMode(message.Mode);
    }

    /// <summary>For testing: simulate a button activation without gaze dwell.</summary>
    public void SimulateButtonActivated(string buttonId) => OnTargetActivated(buttonId);

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
        }
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = progress;
    }
}
