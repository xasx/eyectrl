namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
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
    // Scripted-collection ground truth. Null in every normal run — attached only when the operator
    // turned the diagnostics setting on for a measurement session.
    private readonly IGroundTruthSink? _groundTruth;
    private readonly string _providerType;
    // Carries the live RecordRawGaze gate. The sidecar describes samples the CSV recorder captured, so
    // the two must start and stop together: a sidecar covering samples that were never recorded is a
    // ground truth for a recording that does not exist.
    private readonly DiagnosticsSettings? _diagnostics;

    private int _sessionGeneration;
    // Ticks (Environment.TickCount64) of the current target's first display. The target is held
    // at least TargetHoldMs (a visible floor), but advances as soon as the collector has enough
    // post-arrival samples after that — the user's pace, not a timer's.
    private long _targetShownAtTicks;

    // Absolute per-target ceiling so a user who never finds the point cannot stall a session
    // forever: the target then advances with whatever was collected (possibly nothing → the
    // session's existing failure paths take over), which is exactly the old timer behavior as
    // the degraded case.
    //
    // Raised from 10 s on 2026-09-13. In-band samples accumulate across separate holds, and this
    // user's holds last a median 0.24 s with searching in between, so filling a target is now a
    // several-attempt affair by design. Ten seconds would have cut off exactly the user this retune
    // is for — and cut them off with a failure, since a target that reaches the cap with nothing
    // in band fails the session.
    private const int TargetSafetyCapMs = 25_000;

    // ...but only once the user has actually reached the point. Waiting 25 s for gaze that never
    // arrives is 25 s of a user staring at a dot the tracker cannot follow — and with five targets,
    // over two minutes before the session admits something is wrong. Arrival is the signal that the
    // long budget is worth spending: before it, the problem is aim or tracking and more time does not
    // help; after it, the user is banking samples and time is exactly what they need.
    private const int NoArrivalCapMs = 8_000;

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

    /// <summary>
    /// The tolerance radius in PHYSICAL pixels — how far from the dot gaze may sit and still count.
    /// The overlay draws a ring of exactly this size, which is the whole point: it is the same number
    /// the collector enforces, read from the same place, so what the user is shown is what they are
    /// being held to.
    /// </summary>
    [ObservableProperty] private double _bandRadiusPixels;

    /// <summary>
    /// Radius of the aim DOT in physical pixels, from <see cref="CalibrationSettings.TargetRadiusPixels"/>.
    /// Distinct from <see cref="BandRadiusPixels"/>, and much smaller: the dot says where to look, the
    /// band says how close counts. Surfaced because the setting was previously read by nothing — the
    /// overlay hard-coded the dot size — so changing it did nothing.
    /// </summary>
    [ObservableProperty] private double _targetRadiusPixels = 20;

    /// <summary>
    /// How much of the current target is collected, 0–1, measured in IN-BAND samples rather than
    /// elapsed time. Progress therefore stalls while the user is off target and resumes where it left
    /// off — which is honest, and is also the feedback that tells them holding is what advances it.
    /// </summary>
    [ObservableProperty] private double _targetProgress;

    /// <summary>
    /// Whether gaze is inside the band right now. The overlay uses it to make "inside" and "about to
    /// leave" legible without text, for a user who cannot read a number and hold a fixation at once.
    /// </summary>
    [ObservableProperty] private bool _isOnTarget;

    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider,
        PhysicalPoint screenSize,
        string monitorDeviceName,
        string calibrationFilePath,
        Func<string>? monitorHardwareId = null,
        ILogger<CalibrationViewModel>? logger = null,
        IGroundTruthSink? groundTruth = null,
        string providerType = "",
        DiagnosticsSettings? diagnostics = null)
        : base(messenger)
    {
        _logger = logger;
        _groundTruth = groundTruth;
        _providerType = providerType;
        _diagnostics = diagnostics;
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
        _targetRadiusPixels = settings.TargetRadiusPixels;
        _bandRadiusPixels = CalibrationGeometry.BandRadiusPx(screenSize);
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
        _groundTruth?.BeginSession(_providerType, _screenSize, _settings.SettleWindowMs);
        CurrentTargetIndex = 0;
        ProgressPercent = 0;
        // Published once per session from the same helper the collector uses, so the ring the overlay
        // draws cannot drift from the tolerance being enforced.
        BandRadiusPixels = CalibrationGeometry.BandRadiusPx(_screenSize);
        TargetProgress = 0;
        IsOnTarget = false;
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

        TargetProgress = 0;
        IsOnTarget = false;

        _gazeProvider.GazePointReceived += OnRawGaze;
        _targetShownAtTicks = Environment.TickCount64;
        var generation = _sessionGeneration;

        // Safety cap, not the pacer: samples advance the target once the collector has enough IN-BAND
        // ones (see OnRawGaze). This only fires when the user cannot finish the target.
        //
        // Two stages, because "never got there" and "got there and is still working at it" deserve
        // different patience. The first expires early and gives up unless gaze has arrived; the second
        // spends the full budget on a user who is making progress.
        ScheduleSafetyCap(generation, Math.Max(_settings.TargetHoldMs, NoArrivalCapMs), isFinal: false);
    }

    /// <summary>
    /// Arms one stage of the per-target safety cap.
    /// </summary>
    /// <param name="generation">Session generation captured when the target opened; a cancel or
    /// restart bumps it and this fires into nothing.</param>
    /// <param name="delayMs">How long this stage waits.</param>
    /// <param name="isFinal">
    /// False for the first stage, which only gives up if gaze never arrived; true for the second,
    /// which advances regardless.
    /// </param>
    private void ScheduleSafetyCap(int generation, int delayMs, bool isFinal)
    {
        Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (IsDisposed) return;
            PostToSyncContext(() =>
            {
                if (generation != _sessionGeneration || State != CalibrationState.Collecting) return;

                // Arrived and still collecting: extend once to the full budget rather than cutting off
                // a user who is banking samples a few at a time.
                if (!isFinal && _calibrationService.HasArrivedAtTarget)
                {
                    ScheduleSafetyCap(generation, Math.Max(1, TargetSafetyCapMs - delayMs), isFinal: true);
                    return;
                }

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
            // Announced exactly as a failure at fit time is (see CompleteSession's catch). The two
            // paths used to differ — this one sent only the state change — which was survivable while
            // a target could only fail by collecting nothing after arriving. Since collection became
            // in-band gated this is the COMMON failure: a user who cannot hold a point fails here, not
            // at the fit. A listener that learns about one kind of failure and not the other would
            // show a stale "calibrating" state for the ordinary case.
            _calibrationService.CancelSession();
            _groundTruth?.AbandonSession();
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    private void OnRawGaze(GazePoint gaze)
    {
        // Scripted ground truth, when a recorder is attached (opt-in diagnostics only; null in every
        // normal run, so calibration behaves exactly as it always has). Recorded here rather than from
        // a timer because the window has to be expressed in the SAME device clock as the raw recording
        // it will be aligned against — this callback has that timestamp in hand.
        //
        // ABOVE the IsValid return on purpose: the window is "when this target was displayed", which
        // includes the moments the tracker lost the eyes. Counting only valid samples would make a
        // target shown entirely during tracking loss vanish from the sidecar altogether — silently
        // absent rather than present-and-empty — and the operator's "~9 windows" check could not tell
        // that apart from a protocol slip. Arrival is unaffected: an invalid sample never reaches the
        // collector, so HasArrivedAtTarget simply reports its existing state.
        //
        // Gated on the live setting, like the CSV recorder's own per-sample gate: the sidecar and the
        // recording it describes must stop together, or a mid-session toggle-off leaves a ground truth
        // covering samples nothing recorded.
        // Spelled out rather than `_diagnostics?.RecordRawGaze != false`: the two read identically but
        // the short form's null case is easy to read backwards, and getting it wrong means a sidecar
        // written for a session that was never recorded. No settings object means no gate to consult —
        // the recorder is injected only for a measurement session in the first place.
        if (_diagnostics is null || _diagnostics.RecordRawGaze)
        {
            _groundTruth?.OnSample(
                CurrentTargetIndex, CurrentTargetPosition,
                gaze.TimestampMicroseconds, _calibrationService.HasArrivedAtTarget);
        }

        if (!gaze.IsValid) return;
        _calibrationService.AddRawSample(new PhysicalPoint(gaze.X, gaze.Y));

        // Feedback, published per sample. Progress counts IN-BAND samples, so it stalls while the user
        // is off target and resumes where it left off rather than restarting — a user who cannot hold
        // for a full window still sees every successful moment bank. IsOnTarget is the live version of
        // the same fact, and is what lets the overlay say "you are on it" without words.
        //
        // Computed against the same gaze sample the collector just consumed, so the ring cannot show
        // "on target" for a sample the collector rejected.
        IsOnTarget = Distance(gaze, CurrentTargetPosition) <= BandRadiusPixels;
        TargetProgress = _settings.SamplesPerPoint <= 0
            ? 0
            : Math.Min(1.0, _calibrationService.CurrentTargetInBandSamples / (double)_settings.SamplesPerPoint);

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

    private static double Distance(GazePoint gaze, PhysicalPoint target)
    {
        var dx = gaze.X - target.X;
        var dy = gaze.Y - target.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
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

            // After the calibration is safely persisted: a sidecar failure must not be able to cost
            // the calibration itself, and the ground truth is only meaningful for a session that
            // actually completed.
            _groundTruth?.CompleteSession();
            LogSigmaReport(data);
        }
        catch (Exception ex)
        {
            _calibrationService.CancelSession();
            _groundTruth?.AbandonSession();
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    /// <summary>
    /// Logs the per-target σ this session just measured, plus the centre-versus-corner split.
    /// <para>
    /// The numbers were always there — <c>CalibrationPoint.StdDevX/Y</c> is the raw per-axis noise of
    /// the samples held on each target — but nothing ever read them out, so the σ that gates every
    /// filter claim in the gaze-jitter debate stayed unknown on this device. Logged unconditionally
    /// after a successful calibration: it costs one line, needs no diagnostics flag, and answers the
    /// question the next measurement session would otherwise have to ask again.
    /// </para>
    /// </summary>
    private void LogSigmaReport(CalibrationData data)
    {
        if (_logger is null) return;
        try
        {
            var report = CalibrationSigmaReport.Build(data);
            _logger.LogInformation("Calibration noise — {Summary}", report.Describe());
            _logger.LogInformation("Calibration noise per target — {Targets}", report.DescribeTargets());
        }
        catch (Exception ex)
        {
            // Diagnostics must never cost a calibration that already succeeded and persisted.
            _logger.LogWarning(ex, "Could not summarise calibration noise; the calibration itself is unaffected");
        }
    }

    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _sessionGeneration++;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _calibrationService.CancelSession();
        _groundTruth?.AbandonSession();
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
