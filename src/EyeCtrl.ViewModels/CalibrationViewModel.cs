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
    private readonly SynchronizationContext? _syncContext;
    private bool _disposed;

    [ObservableProperty] private CalibrationState _state = CalibrationState.Idle;
    [ObservableProperty] private int _currentTargetIndex;
    [ObservableProperty] private int _totalTargets;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private PhysicalPoint _currentTargetPosition;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private double? _meanErrorPixels;
    [ObservableProperty] private string _targetColor = "#00FF00";

    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider,
        PhysicalPoint screenSize,
        string monitorDeviceName)
    {
        _settings = settings;
        _calibrationService = calibrationService;
        _messenger = messenger;
        _gazeProvider = gazeProvider;
        _screenSize = screenSize;
        _monitorDeviceName = monitorDeviceName;
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
        _calibrationService.BeginTarget(CurrentTargetIndex, _screenSize);

        _messenger.Send(new CalibrationTargetPositionMessage(pos, CurrentTargetIndex, TotalTargets));
        _messenger.Send(new CalibrationStateChangedMessage(State, (CurrentTargetIndex / (double)TotalTargets) * 100));

        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            _gazeProvider.GazePointReceived -= OnRawGaze;
            PostToSyncContext(() =>
            {
                if (State != CalibrationState.Collecting) return;
                try
                {
                    _calibrationService.EndTarget();
                    CurrentTargetIndex++;
                    ProgressPercent = (CurrentTargetIndex / (double)TotalTargets) * 100;
                    MoveToNextTarget();
                }
                catch (Exception ex)
                {
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
            State = CalibrationState.Active;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(true, errorPx));

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var filePath = System.IO.Path.Combine(appData, "EyeCtrl", "calibration.json");
            _calibrationService.Save(filePath, data);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _calibrationService.CancelSession();
        State = CalibrationState.Idle;
        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
        _messenger.Send(new CalibrationCancelledMessage());
    }

    public void ClearCalibration()
    {
        _calibrationService.Clear();
        _messenger.Send(new CalibrationCompletedMessage(false, null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
