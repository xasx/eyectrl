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
    IRecipient<MeasureOnlyCalibrationRequestedMessage>,
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
    // Scripted-collection ground truth. A real recorder since ticket 6 — whether it actually records
    // is still gated per session by SidecarEnabled below.
    private readonly IGroundTruthSink? _groundTruth;
    private readonly string _providerType;
    // Live sidecar gate. Normally the sidecar describes samples the CSV recorder captured, so the two
    // must start and stop together: a sidecar covering samples that were never recorded is a ground
    // truth for a recording that does not exist. The measure-only probe is the deliberate exception —
    // its sidecar is self-contained (each window carries its own raw samples) and the user started it
    // from its own button, so it must not hinge on a global diagnostics flag that a gaze-only user
    // cannot see, let alone toggle.
    private readonly DiagnosticsSettings? _diagnostics;

    private int _sessionGeneration;
    // Ticks (Environment.TickCount64) of the current target's first display. The target is held
    // at least TargetHoldMs (a visible floor), but advances as soon as the collector has enough
    // post-arrival samples after that — the user's pace, not a timer's.
    private long _targetShownAtTicks;

    // Whether this session is the measure-only bias probe. Per session, never persisted: a settings
    // flag that stays on would make the next deliberate "Calibrate Now" silently skip the transform.
    private bool _measureOnly;
    // Whether any target of a measure-only session ended with nothing collected. Turns the session's
    // recorded outcome into Partial — the decision rule needs the honest count of usable windows.
    private bool _measureOnlyAnyEmptyTarget;

    /// <summary>
    /// The probe always visits five targets. Fixed rather than read from <c>CalibrationSettings.PointCount</c>
    /// because the pre-registered decision rule is "|b| ≥ 60 px at ≥ 2 of 5 targets, or ≥ 75 px at the
    /// centre": a session with a different count could not be judged against it.
    /// </summary>
    private const int MeasureOnlyPointCount = 5;

    /// <summary>
    /// How long a measure-only target stays up, whatever the gaze does. The probe needs about one to
    /// three seconds of samples per target, and this user cannot press Esc: their only other way out
    /// of a full-screen backdrop is to wait out the normal caps (8 s, or 25 s once arrived), which
    /// would be up to two minutes of effortful fixation that the data does not need. Fixed at 5 s, so
    /// the whole probe is about 25 s. Normal calibration keeps its two-stage 8 s / 25 s caps.
    /// <para>
    /// 5 s, not the original 3 s: the first real probe (2026-09-26) showed this user reaching each
    /// target 0.8–2.6 s after it appeared, so a 3 s window left 0.4–2 s of hold and clusters of only
    /// 8–23 samples. The extra 2 s is all hold time.
    /// </para>
    /// </summary>
    private const int MeasureOnlyTargetBudgetMs = 5_000;

    /// <summary>Whether the ground-truth sidecar collects for the current session. See the field remarks.</summary>
    private bool SidecarEnabled => _measureOnly || _diagnostics is null || _diagnostics.RecordRawGaze;

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

    /// <summary>
    /// Whether the running session is the measure-only bias probe. The overlay reads it to end with a
    /// neutral "recorded" panel — no failure text, no Retry — and the view model uses it to skip the
    /// fit, the save and the transform entirely.
    /// </summary>
    [ObservableProperty] private bool _isMeasureOnly;

    /// <summary>
    /// Every target position for this session, in the order they will be presented. Empty when idle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published so the overlay can render the WHOLE set for the session instead of one dot at a time.
    /// The user reported having to "search where the next point is" after every advance; showing the
    /// upcoming targets means the next location is known before the current one completes, and the
    /// active target can move there visibly rather than teleporting.
    /// </para>
    /// <para>
    /// Set once per session and not replaced on advance — the collection is stable so a view can build
    /// its visuals once and only restyle them as <see cref="CurrentTargetIndex"/> moves. Comes from
    /// <c>CalibrationService.TargetPositions</c>, the same list the collector gates against, so what is
    /// drawn cannot drift from what is enforced.
    /// </para>
    /// </remarks>
    [ObservableProperty] private IReadOnlyList<PhysicalPoint> _targetPositions = Array.Empty<PhysicalPoint>();

    /// <summary>
    /// The typed failure that ended the last session, or null when none did. Kept typed so the words
    /// the overlay shows come from one renderer (<see cref="CalibrationFailureText"/>) and a test can
    /// assert the code and its arguments rather than the sentence.
    /// </summary>
    public CalibrationFailure? Failure { get; private set; }

    /// <summary>
    /// The failure rendered as the sentence the completion panel shows, or null when no failure ended
    /// the session. Computed from <see cref="Failure"/> so the typed value and the words cannot drift.
    /// </summary>
    public string? ErrorText => Failure is null ? null : CalibrationFailureText.Describe(Failure);

    /// <summary>
    /// What the target at <paramref name="index"/> is doing, derived from <see cref="CurrentTargetIndex"/>.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so it cannot disagree with the index that actually drives collection.
    /// Outside a collecting session everything reads <see cref="CalibrationTargetStatus.Upcoming"/>:
    /// during Training and Active the targets are no longer shown at all, and claiming "Current" for one
    /// of them would invite a view to keep drawing a live ring over a finished session.
    /// </remarks>
    public CalibrationTargetStatus StatusOf(int index)
    {
        if (State != CalibrationState.Collecting) return CalibrationTargetStatus.Upcoming;
        if (index < CurrentTargetIndex) return CalibrationTargetStatus.Done;
        return index == CurrentTargetIndex
            ? CalibrationTargetStatus.Current
            : CalibrationTargetStatus.Upcoming;
    }

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
        StartSession(measureOnly: false);
    }

    public void Receive(MeasureOnlyCalibrationRequestedMessage message)
    {
        if (State != CalibrationState.Idle) return;
        StartSession(measureOnly: true);
    }

    public void Receive(CalibrationClearRequestedMessage message)
    {
        ClearCalibration();
    }

    private void StartSession(bool measureOnly)
    {
        _sessionGeneration++;
        _measureOnly = measureOnly;
        IsMeasureOnly = measureOnly;
        _measureOnlyAnyEmptyTarget = false;
        TotalTargets = _calibrationService.StartSession(
            measureOnly ? MeasureOnlyPointCount : _settings.PointCount, _screenSize);
        // Taken from the service, not recomputed here: the overlay draws these and the collector gates
        // on them, and two independent calls to the position helper is exactly how a drawn target and
        // an enforced target come to differ.
        TargetPositions = _calibrationService.TargetPositions;
        if (SidecarEnabled)
            _groundTruth?.BeginSession(
                _providerType, _screenSize, _settings.SettleWindowMs,
                _measureOnly ? GroundTruthMode.MeasureOnly : GroundTruthMode.Calibration);
        CurrentTargetIndex = 0;
        ProgressPercent = 0;
        // Published once per session from the same helper the collector uses, so the ring the overlay
        // draws cannot drift from the tolerance being enforced.
        BandRadiusPixels = CalibrationGeometry.BandRadiusPx(_screenSize);
        TargetProgress = 0;
        IsOnTarget = false;
        Failure = null;
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

        // The target's clock starts BEFORE the handler is attached: a tracker-thread sample can land the
        // instant it is, and it must be timed against this target, not the previous one (whose start is
        // ~5 s old, so a probe's time ring would flash full on the new dot — review A's FU10-1).
        _targetShownAtTicks = Environment.TickCount64;
        _gazeProvider.GazePointReceived += OnRawGaze;
        var generation = _sessionGeneration;

        // Safety cap, not the pacer: samples advance the target once the collector has enough IN-BAND
        // ones (see OnRawGaze). This only fires when the user cannot finish the target.
        //
        // Two stages, because "never got there" and "got there and is still working at it" deserve
        // different patience. The first expires early and gives up unless gaze has arrived; the second
        // spends the full budget on a user who is making progress. The probe replaces both with one
        // short fixed budget — see MeasureOnlyTargetBudgetMs.
        ScheduleSafetyCap(
            generation,
            CurrentTargetIndex,
            _measureOnly ? MeasureOnlyTargetBudgetMs : Math.Max(_settings.TargetHoldMs, NoArrivalCapMs),
            isFinal: _measureOnly);
    }

    /// <summary>
    /// Arms one stage of the per-target safety cap.
    /// </summary>
    /// <param name="generation">Session generation captured when the target opened; a cancel or
    /// restart bumps it and this fires into nothing.</param>
    /// <param name="targetIndex">
    /// The target this stage was armed for. A cap OUTLIVES the target it was armed for whenever the
    /// gaze path advances first, so it carries the index it was armed for and
    /// <see cref="AdvanceFromCurrentTarget"/> no-ops when that target is no longer current. (An
    /// earlier extra early-return here claimed to stop a stale cap extending a later target's budget;
    /// it cannot — only a target's own caps govern it — and was removed.)
    /// </param>
    /// <param name="delayMs">How long this stage waits.</param>
    /// <param name="isFinal">
    /// False for the first stage, which only gives up if gaze never arrived; true for the second,
    /// which advances regardless.
    /// </param>
    private void ScheduleSafetyCap(int generation, int targetIndex, int delayMs, bool isFinal)
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
                    ScheduleSafetyCap(generation, targetIndex, Math.Max(1, TargetSafetyCapMs - delayMs), isFinal: true);
                    return;
                }

                AdvanceFromCurrentTarget(targetIndex);
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
            // Measure-only: a target the user never got within the band of is one window of evidence,
            // not a failed session. The collector refuses to produce a point from zero in-band samples,
            // so it is discarded here and the probe moves on — ending at the first empty target would
            // throw away exactly the windows the five-target decision rule needs.
            if (_measureOnly && _calibrationService.CurrentTargetInBandSamples <= 0)
            {
                _calibrationService.DiscardTarget();
                _measureOnlyAnyEmptyTarget = true;
                _logger?.LogInformation(
                    "Measure-only target {TargetIndex} collected nothing; its window is kept and the probe moves on",
                    expectedIndex);
            }
            else
            {
                _calibrationService.EndTarget();
            }

            CurrentTargetIndex++;
            ProgressPercent = (CurrentTargetIndex / (double)TotalTargets) * 100;
            MoveToNextTarget();
        }
        catch (Exception ex)
        {
            if (_measureOnly)
            {
                // The probe cannot fail: whatever fault occurred, the sidecar is written with the
                // failure outcome and the user sees the same neutral ending as any other run.
                _logger?.LogWarning(ex,
                    "Measure-only probe ended early at target {TargetIndex}; the data gathered is kept",
                    expectedIndex);
                CompleteMeasureOnlySession(GroundTruthOutcome.Failed);
                return;
            }

            // Announced exactly as a failure at fit time is (see CompleteSession's catch). The two
            // paths used to differ — this one sent only the state change — which was survivable while
            // a target could only fail by collecting nothing after arriving. Since collection became
            // in-band gated this is the COMMON failure: a user who cannot hold a point fails here, not
            // at the fit. A listener that learns about one kind of failure and not the other would
            // show a stale "calibrating" state for the ordinary case.
            _calibrationService.CancelSession();
            _groundTruth?.AbandonSession();
            // The typed failure, not the exception text: the renderer owns the words (they must be
            // translatable, and raw exception text never is), and the technical message stays in the
            // log. An exception outside the typed set (a defect) still gets a renderable code.
            _logger?.LogWarning(ex, "Calibration failed at target {TargetIndex}", expectedIndex);
            Failure = (ex as CalibrationFailedException)?.Failure ?? CalibrationFailure.Unexpected;
            State = CalibrationState.Failed;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    private void OnRawGaze(GazePoint gaze)
    {
        // Scripted ground truth, when the sidecar is enabled for this session (SidecarEnabled below).
        // Recorded here rather than from a timer because the window has to be expressed in the SAME
        // device clock as the raw recording it will be aligned against — this callback has that
        // timestamp in hand.
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
        // covering samples nothing recorded. The measure-only probe bypasses this through
        // SidecarEnabled — its samples live in the sidecar itself.
        //
        // Spelled out rather than `_diagnostics?.RecordRawGaze != false`: the two read identically but
        // the short form's null case is easy to read backwards, and getting it wrong means a sidecar
        // written for a session that was never recorded. No settings object means no gate to consult —
        // which is how the probe's tests drive it.
        if (SidecarEnabled)
        {
            _groundTruth?.OnSample(
                CurrentTargetIndex, CurrentTargetPosition,
                gaze, _calibrationService.HasArrivedAtTarget);
        }

        // Measure-only's ring shows TIME, so it keeps moving through blinks and tracking loss: computed
        // here, above the validity return, from any sample the tracker delivers (review A's FU10-2 — below
        // the return it froze on a blink and read 0 for a window whose gaze was lost throughout).
        if (_measureOnly)
            TargetProgress = Math.Clamp(
                (Environment.TickCount64 - _targetShownAtTicks) / (double)MeasureOnlyTargetBudgetMs, 0, 1);

        if (!gaze.IsValid) return;
        _calibrationService.AddRawSample(new PhysicalPoint(gaze.X, gaze.Y));

        // Feedback, published per sample. Progress counts IN-BAND samples, so it stalls while the user
        // is off target and resumes where it left off rather than restarting — a user who cannot hold
        // for a full window still sees every successful moment bank. IsOnTarget is the live version of
        // the same fact, and is what lets the overlay say "you are on it" without words.
        //
        // Computed against the same gaze sample the collector just consumed, so the ring cannot show
        // "on target" for a sample the collector rejected.
        //
        // Measure-only is paced by its fixed window, not by samples, so its ring shows the elapsed share
        // of that window instead (set above the validity return): it fills exactly when the dot moves on.
        // Counting in-band samples there saturated the ring in about 0.5 s at defaults and then sat full
        // for the rest of the 5 s — "done, stuck" to anyone watching (holistic review N2). IsOnTarget
        // still says "you are on it".
        IsOnTarget = Distance(gaze, CurrentTargetPosition) <= BandRadiusPixels;
        if (!_measureOnly)
        {
            TargetProgress = _settings.SamplesPerPoint <= 0
                ? 0
                : Math.Min(1.0, _calibrationService.CurrentTargetInBandSamples / (double)_settings.SamplesPerPoint);
        }

        // The user's pace: advance the moment the target has its samples, past the visible floor.
        // Before this, a fixed timer advanced targets whether or not the user had arrived, and samples
        // were averaged from wherever gaze happened to be.
        //
        // Measure-only does NOT use the user's pace: its window is a fixed budget, so an in-band gaze
        // cannot shorten it. That matters at low calibration settings, where completion arrives after
        // ~5 samples and the window would be under 200 ms — too short for the estimator to find any
        // cluster, so every small-bias probe would come back "no steady holds".
        if (!_measureOnly
            && _calibrationService.IsTargetComplete
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
        if (_measureOnly)
        {
            CompleteMeasureOnlySession(_measureOnlyAnyEmptyTarget
                ? GroundTruthOutcome.Partial
                : GroundTruthOutcome.Completed);
            return;
        }

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
            // Same typed hand-off as the per-target path above: the renderer owns the user's words,
            // the technical message goes to the log. A fault thrown after the fit succeeds (e.g. the
            // save) is outside the typed set and becomes Unexpected.
            _logger?.LogWarning(ex, "Calibration failed while completing the session");
            Failure = (ex as CalibrationFailedException)?.Failure ?? CalibrationFailure.Unexpected;
            State = CalibrationState.Failed;
            Messenger.Send(new CalibrationStateChangedMessage(State, 100));
            Messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    /// <summary>
    /// Ends a measure-only probe: no fit, no save, no transform, and the self-contained sidecar
    /// written whatever happened.
    /// </summary>
    /// <remarks>
    /// The whole point of the mode. A normal calibration's terminal state means something was computed
    /// and either applied or refused; the probe has no such state, so it always ends Active with no
    /// <c>CurrentData</c> change, and the sidecar's <c>outcome</c> field — not the UI — says how it
    /// went. That is what lets the same neutral screen serve a complete run, a partial one, an
    /// abandonment and a fault: the user who finds fixation hard never reads the word "failed".
    /// </remarks>
    private void CompleteMeasureOnlySession(GroundTruthOutcome outcome)
    {
        State = CalibrationState.Training;
        Messenger.Send(new CalibrationStateChangedMessage(State, 100));

        try
        {
            // Clears the collector/target state only; unlike CompleteSession it fits nothing and never
            // touches CurrentTransform, so a previous calibration stays exactly as it was.
            _calibrationService.CancelSession();
            _groundTruth?.CompleteSession(outcome);
        }
        catch (Exception ex)
        {
            // Best-effort by construction: the recorder already swallows write faults, and a custom
            // sink that throws must not turn a neutral ending into a failure screen.
            _logger?.LogWarning(ex,
                "Measure-only probe could not persist its sidecar; the probe still ends neutrally");
        }

        // Active, not a new state: OverlayMessageCoordinator's Active branch is what releases fixation
        // suppression and restores the toolbar, and the overlay's IsMeasureOnly branch draws the
        // neutral panel. Ending in Active is also what keeps that release running exactly once per
        // session, the same as a normal calibration.
        State = CalibrationState.Active;
        Messenger.Send(new CalibrationStateChangedMessage(State, 100));
        Messenger.Send(new CalibrationCompletedMessage(true, null));
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
        try
        {
            if (_measureOnly)
            {
                // Abandonment is not failure: the windows gathered so far are kept, and the outcome says
                // why they stop where they do. Restoring AbandonSession here is the named mutation — it
                // would throw away the only data the probe exists to collect.
                _groundTruth?.CompleteSession(GroundTruthOutcome.Abandoned);
            }
            else
            {
                _groundTruth?.AbandonSession();
            }
        }
        catch (Exception ex)
        {
            // Idle is the coordinator's ONLY suppression release for an abandon, so a sink that throws
            // must not be allowed to skip it: the view model would stay in Collecting behind a
            // full-screen backdrop with no gaze subscription and no live caps.
            _logger?.LogWarning(ex, "The sidecar sink threw while abandoning; the session still ends");
        }
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
        StartSession(_measureOnly);
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
