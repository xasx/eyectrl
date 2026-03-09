namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ToolbarViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>
{
    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;
    private readonly Dictionary<string, ToolbarButtonViewModel> _buttonLookup = new();

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isZoomOn;

    public ObservableCollection<ToolbarButtonViewModel> Buttons { get; } = new();

    /// <summary>
    /// Screen positions of buttons, set by the View after layout.
    /// Keys are button IDs, values are screen-coordinate centers.
    /// </summary>
    public void UpdateButtonScreenPosition(string buttonId, ScreenPoint screenCenter)
    {
        _dwellDetector.UpdateTargetCenter(buttonId, screenCenter);
    }

    public ToolbarViewModel(ToolbarSettings toolbarSettings, DwellSettings dwellSettings, IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(dwellSettings.DwellCooldownMs);

        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "\u23F8"),
            ("Zoom", "\u2295"),
            ("Keyboard", "\u2328"),
            ("Settings", "\u2699"),
        };

        foreach (var (id, symbol) in buttons)
        {
            var btn = new ToolbarButtonViewModel(id, symbol);
            Buttons.Add(btn);
            _buttonLookup[id] = btn;
            _dwellDetector.AddTarget(new DwellTarget(id,
                new ScreenPoint(0, 0), toolbarSettings.Size / 2, dwellSettings.ToolbarButtonDwellMs));
        }

        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.Symbol = IsPaused ? "\u25B6" : "\u23F8";
    }

    /// <summary>For testing: simulate a button activation without gaze dwell.</summary>
    public void SimulateButtonActivated(string buttonId) => OnTargetActivated(buttonId);

    private void OnTargetActivated(string targetId)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = 0;

        switch (targetId)
        {
            case "Pause": _messenger.Send(new PauseToggledMessage()); break;
            case "Zoom": _messenger.Send(new ZoomToggledMessage()); break;
            case "Keyboard": _messenger.Send(new KeyboardToggledMessage()); break;
            case "Settings": _messenger.Send(new SettingsRequestedMessage()); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = progress;
    }
}
