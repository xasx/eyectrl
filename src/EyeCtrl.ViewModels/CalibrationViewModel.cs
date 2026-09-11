namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<CalibrationViewModel>? _logger;

    private int _sessionGeneration;
    // Ticks (Environment.TickCount64) of the current target's first display. The target is held
    // at least TargetHoldMs (a visible floor), but advances as soon as the collector has enough
    // post-arrival samples after that — the user's pace, not a timer's.
    private long _targetShownAtTicks;

    // Absolute per-target ceiling so a user who never finds the point cannot stall a session
    // forever: the target then advances with whatever was collected (possibly nothing → the
    // session's existing failure paths take over), which is exactly the old timer behavior as
    // the degraded case.
    private const int TargetSafetyCapMs = 10_000;

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
        Func<string>? monitorHardwareId = null,
        ILogger<CalibrationViewModel>? logger = null)
        : base(messenger)
    {
        _logger = logger;
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
        _targetShownAtTicks = Environment.TickCount64;
        var generation = _sessionGeneration;

        // Safety cap, not the pacer: samples advance the target once the collector is complete
        // (see OnRawGaze). This timer only fires when the user never arrives at the point.
        var capMs = Math.Max(_settings.TargetHoldMs, TargetSafetyCapMs);
        Task.Delay(capMs).ContinueWith(_ =>
        {
            if (IsDisposed) return;
            PostToSyncContext(() =>
            {
                if (generation != _sessionGeneration || State != CalibrationState.Collecting) return;
                AdvanceFromCurrentTarget(CurrentTargetIndex);
            });
        });
    }

    /// <summary>
    /// Ends the current target and moves on. Called from the gaze path (when the collector
    /// reports enough post-arrival samples and the minimum display time has passed) and from the
    /// safety-cap timer. <paramref name="expectedIndex"/> collapses the race between the two:
    /// whichever runs first moves the index, and the other's post then no-ops instead of ending
    /// the NEXT target early.
    /// </summary>
    private void AdvanceFromCurrentTarget(int expectedIndex)
    {
        if (State != CalibrationState.Collecting || CurrentTargetIndex != expectedIndex) return;

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
    }

    private void OnRawGaze(GazePoint gaze)
    {
        if (!gaze.IsValid) return;
        _calibrationService.AddRawSample(new PhysicalPoint(gaze.X, gaze.Y));

        // The user's pace: advance the moment the target has its samples, past the visible
        // floor. Before this, a fixed timer advanced targets whether or not the user had
        // arrived, and samples were averaged from wherever gaze happened to be.
        if (_calibrationService.IsTargetComplete
            && Environment.TickCount64 - _targetShownAtTicks >= _settings.TargetHoldMs)
        {
            var index = CurrentTargetIndex;
            PostToSyncContext(() => AdvanceFromCurrentTarget(index));
        }
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
        //
        // Logged, because this is the ONLY thing that deletes calibration.json and it used to do so
        // behind a bare `catch { }`. A missing calibration and a calibration that failed to load are
        // indistinguishable afterwards — gaze just runs raw — so without this line "where did my
        // calibration go?" cannot be answered from the logs at all. (It was a deliberate Clear, not a
        // torn write, that prompted the investigation this came out of.)
        try
        {
            if (System.IO.File.Exists(_calibrationFilePath))
            {
                System.IO.File.Delete(_calibrationFilePath);
                _logger?.LogInformation(
                    "Calibration cleared by the user; deleted {FilePath}. Gaze runs uncalibrated until you recalibrate",
                    _calibrationFilePath);
            }
            else
            {
                _logger?.LogInformation(
                    "Calibration cleared by the user; no persisted file at {FilePath} to delete",
                    _calibrationFilePath);
            }
        }
        catch (Exception ex)
        {
            // Warning, not Information: the in-memory calibration is gone but the file survived, so the
            // next startup silently reloads what the user just discarded — a visible contradiction.
            _logger?.LogWarning(ex,
                "Calibration cleared, but {FilePath} could not be deleted; it will be reloaded on the next start",
                _calibrationFilePath);
        }

        // Neutral reset (not a failure) so the toolbar icon returns to the idle state.
        Messenger.Send(new CalibrationCancelledMessage());
    }

    protected override void OnDispose()
    {
        _gazeProvider.GazePointReceived -= OnRawGaze;
    }
}
