namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CalibrationViewModel : MessengerViewModelBase,
    IRecipient<CalibrationRequestedMessage>,
    IRecipient<CalibrationClearRequestedMessage>
{
    private readonly CalibrationSettings _settings;
    private readonly CalibrationService _calibrationService;
    private readonly IGazeProvider _gazeProvider;
    private readonly PhysicalPoint _screenSize;
    private readonly string _monitorDeviceName;
    // Persisted alongside the device name so a later load survives display renumbering; empty when the
    // platform could not read the monitor's EDID, which falls the match back to the device name.
    //
    // Resolved when the session completes, NOT when this view model is constructed. A hardware id
    // captured at startup names the monitor that was primary then, and CalibrationService.Load
    // prefers the id over the device name — so persisting a stale one would reject a perfectly good
    // calibration on the next launch, the exact failure the id exists to prevent.
    private readonly Func<string> _monitorHardwareId;
    private readonly string _calibrationFilePath;
    private readonly SynchronizationContext? _syncContext;

    private int _sessionGeneration;

    [ObservableProperty] private CalibrationState _state = CalibrationState.Idle;
    [ObservableProperty] private int _currentTargetIndex;
    [ObservableProperty] private int _totalTargets;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private PhysicalPoint _currentTargetPosition;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private double? _meanErrorPixels;
    [ObservableProperty] private double? _qualityPercent;
    // Default mirrors Tokens.Accent / SettingsDefaults.Accent (#A5F3FC); overwritten from
    // settings.TargetColor in the constructor, but the initializer must not carry the
    // outgoing Lime accent this migration removed.
    [ObservableProperty] private string _targetColor = "#A5F3FC";

    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider,
        PhysicalPoint screenSize,
        string monitorDeviceName,
        string calibrationFilePath,
        Func<string>? monitorHardwareId = null)
        : base(messenger)
    {
        _settings = settings;
        _calibrationService = calibrationService;
        _gazeProvider = gazeProvider;
        _screenSize = screenSize;
        _monitorDeviceName = monitorDeviceName;
        _monitorHardwareId = monitorHardwareId ?? (static () => string.Empty);
        // Required, with no %APPDATA% fallback. The App's DI already injects a resolved path
        // (AppBootstrapper), so the fallback was never taken in production — it was a default
        // waiting for the first caller that forgot the argument, and what it would silently write
        // is the live profile's calibration.json. That file is uniquely unforgiving: its transform
        // is applied to every gaze point, so a bad one breaks dwell everywhere at once with no
        // obvious cause. Required beats convenient here.
        _calibrationFilePath = calibrationFilePath;
        _targetColor = settings.TargetColor;
        _syncContext = SynchronizationContext.Current;
    }

    public void Receive(CalibrationRequestedMessage message)
    {
        if (State != CalibrationState.Idle) return;
        StartSession();
    }

    public void Receive(CalibrationClearRequestedMessage message)
    {
        ClearCalibration();
    }

    private void StartSession()
    {
        _sessionGeneration++;
        TotalTargets = _calibrationService.StartSession(_settings.PointCount, _screenSize);
        CurrentTargetIndex = 0;
        ProgressPercent = 0;
        ErrorText = null;
        MeanErrorPixels = null;
        State = CalibrationState.Collecting;

        Messenger.Send(new CalibrationStateChangedMessage(State, 0));
        MoveToNextTarget();
    }

    private void MoveToNextTarget()
    {
        if (State != CalibrationState.Collecting) return;

        if (CurrentTargetIndex >= TotalTargets)
        {
            CompleteSession();
            return;
        }

        var pos = _calibrationService.GetTargetPosition(CurrentTargetIndex);
        CurrentTargetPosition = pos;
        _calibrationService.BeginTarget(CurrentTargetIndex, _settings.SettleWindowMs, _settings.SamplesPerPoint);

        Messenger.Send(new CalibrationTargetPositionMessage(pos, CurrentTargetIndex, TotalTargets));
        Messenger.Send(new CalibrationStateChangedMessage(State, (CurrentTargetIndex / (double)TotalTargets) * 100));

        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;
        var generation = _sessionGeneration;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (IsDisposed) return;
            PostToSyncContext(() =>
            {
                if (generation != _sessionGeneration || State != CalibrationState.Collecting) return;
                _gazeProvider.GazePointReceived -= OnRawGaze;
                try
                {
                    _calibrationService.EndTarget();
                    CurrentTargetIndex++;
                    ProgressPercent = (CurrentTargetIndex / (double)TotalTargets) * 100;
                    MoveToNextTarget();
                }
                catch (Exception ex)
                {
                    _calibrationService.CancelSession();
                    ErrorText = ex.Message;
                    State = CalibrationState.Failed;
                    Messenger.Send(new CalibrationStateChangedMessage(State, 100));
                }
            });
        });
    }

    private void OnRawGaze(GazePoint gaze)
    {
        if (!gaze.IsValid) return;
        _calibrationService.AddRawSample(new PhysicalPoint(gaze.X, gaze.Y));
    }

    private void PostToSyncContext(Action action)
    {
        if (_syncContext != null)
            _syncContext.Post(_ => action(), null);
        else
            action();
    }

    private void CompleteSession()
    {
        State = CalibrationState.Training;
        Messenger.Send(new CalibrationStateChangedMessage(State, 100));

        try
        {
            // Resolved here, against the display that is primary now — see the field's remark.
            var data = _calibrationService.CompleteSession(
                _screenSize, _monitorDeviceName, _monitorHardwareId());
            var errorPx = data.ComputeMeanErrorPixels();
            MeanErrorPixels = errorPx;
            QualityPercent = data.ComputeQualityPercent();
            State = CalibrationState.Active;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(true, errorPx));

            _calibrationService.Save(_calibrationFilePath, data);
        }
        catch (Exception ex)
        {
            _calibrationService.CancelSession();
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _sessionGeneration++;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _calibrationService.CancelSession();
        State = CalibrationState.Idle;
        Messenger.Send(new CalibrationStateChangedMessage(State, 0));
        Messenger.Send(new CalibrationCancelledMessage());
    }

    /// <summary>
    /// Dismisses a completed (Active) or failed result and returns to Idle so a new
    /// calibration can be requested. Without this the VM stays stuck in Active/Failed
    /// and ignores every subsequent calibration request.
    /// </summary>
    public void Acknowledge()
    {
        if (State != CalibrationState.Active && State != CalibrationState.Failed) return;
        State = CalibrationState.Idle;
        Messenger.Send(new CalibrationStateChangedMessage(State, 0));
    }

    /// <summary>Discards a completed/failed result and immediately restarts calibration.</summary>
    public void Retry()
    {
        if (State != CalibrationState.Active && State != CalibrationState.Failed) return;
        State = CalibrationState.Idle;
        StartSession();
    }

    public void ClearCalibration()
    {
        _calibrationService.Clear();
        MeanErrorPixels = null;
        QualityPercent = null;

        // Best-effort deletion of the persisted calibration file so it won't reload on next startup.
        try { if (System.IO.File.Exists(_calibrationFilePath)) System.IO.File.Delete(_calibrationFilePath); }
        catch { /* non-critical */ }

        // Neutral reset (not a failure) so the toolbar icon returns to the idle state.
        Messenger.Send(new CalibrationCancelledMessage());
    }

    protected override void OnDispose()
    {
        _gazeProvider.GazePointReceived -= OnRawGaze;
    }
}
