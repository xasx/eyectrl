namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CalibrationViewModel : ObservableObject,
    IRecipient<CalibrationRequestedMessage>,
    IRecipient<CalibrationClearRequestedMessage>,
    IDisposable
{
    private readonly CalibrationSettings _settings;
    private readonly CalibrationService _calibrationService;
    private readonly IMessenger _messenger;
    private readonly IGazeProvider _gazeProvider;
    private readonly PhysicalPoint _screenSize;
    private readonly string _monitorDeviceName;
    private readonly string _calibrationFilePath;
    private readonly SynchronizationContext? _syncContext;
    private bool _disposed;

    private int _sessionGeneration;

    [ObservableProperty] private CalibrationState _state = CalibrationState.Idle;
    [ObservableProperty] private int _currentTargetIndex;
    [ObservableProperty] private int _totalTargets;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private PhysicalPoint _currentTargetPosition;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private double? _meanErrorPixels;
    [ObservableProperty] private double? _qualityPercent;
    [ObservableProperty] private string _targetColor = "#00FF00";

    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider,
        PhysicalPoint screenSize,
        string monitorDeviceName,
        string? calibrationFilePath = null)
    {
        _settings = settings;
        _calibrationService = calibrationService;
        _messenger = messenger;
        _gazeProvider = gazeProvider;
        _screenSize = screenSize;
        _monitorDeviceName = monitorDeviceName;
        _calibrationFilePath = calibrationFilePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "calibration.json");
        _targetColor = settings.TargetColor;
        _syncContext = SynchronizationContext.Current;
        messenger.RegisterAll(this);
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

        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
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

        _messenger.Send(new CalibrationTargetPositionMessage(pos, CurrentTargetIndex, TotalTargets));
        _messenger.Send(new CalibrationStateChangedMessage(State, (CurrentTargetIndex / (double)TotalTargets) * 100));

        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;
        var generation = _sessionGeneration;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (_disposed) return;
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
                    _messenger.Send(new CalibrationStateChangedMessage(State, 100));
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
        _messenger.Send(new CalibrationStateChangedMessage(State, 100));

        try
        {
            var data = _calibrationService.CompleteSession(_screenSize, _monitorDeviceName);
            var errorPx = data.ComputeMeanErrorPixels();
            MeanErrorPixels = errorPx;
            QualityPercent = data.ComputeQualityPercent();
            State = CalibrationState.Active;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(true, errorPx));

            _calibrationService.Save(_calibrationFilePath, data);
        }
        catch (Exception ex)
        {
            _calibrationService.CancelSession();
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _sessionGeneration++;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _calibrationService.CancelSession();
        State = CalibrationState.Idle;
        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
        _messenger.Send(new CalibrationCancelledMessage());
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
        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
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
        _messenger.Send(new CalibrationCancelledMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
