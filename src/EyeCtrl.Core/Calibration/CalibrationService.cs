namespace EyeCtrl.Core.Calibration;

using System.Text.Json;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages calibration sessions: starts, collects samples per target, fits an affine transform,
/// validates the fit, persists/loads calibration data, and applies the transform.
/// </summary>
public class CalibrationService
{
    // Volatile reference assignment is atomic; no lock needed for a single-field swap.
    private volatile ICalibrationTransform? _currentTransform;
    private readonly ILogger<CalibrationService>? _logger;

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private List<PhysicalPoint>? _targetPositions;
    private IReadOnlyList<PhysicalPoint>? _readOnlyTargetPositions;
    private CalibrationPointCollector? _currentCollector;
    private readonly List<CalibrationPoint> _collectedPoints = new();
    private int _currentTargetIndex;
    private PhysicalPoint _screenSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationService"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public CalibrationService(ILogger<CalibrationService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>The currently active calibration transform, or null if no calibration is applied.</summary>
    public ICalibrationTransform? CurrentTransform => _currentTransform;
    /// <summary>Whether a calibration session is in progress.</summary>
    public bool IsSessionActive => _targetPositions != null;

    /// <summary>
    /// Metadata for the currently active calibration (from the last completed session
    /// or a successful load). Null when no calibration is applied.
    /// </summary>
    public CalibrationData? CurrentData { get; private set; }

    /// <summary>
    /// Why the last <see cref="Load"/> refused to apply a persisted calibration, as a sentence fit to show
    /// the user; null when nothing was rejected (no file, loaded successfully, or no load attempted).
    /// <para>
    /// Exists because a rejected calibration is otherwise indistinguishable from never having calibrated:
    /// <see cref="CurrentTransform"/> is null either way and gaze silently runs raw. The UI pairs this with
    /// <see cref="CurrentData"/> — non-null data means a calibration is active, null data plus a non-null
    /// rejection means one exists on disk but is inert.
    /// </para>
    /// </summary>
    public string? LastLoadRejection { get; private set; }

    /// <summary>
    /// Starts a new calibration session with the specified number of target points.
    /// </summary>
    /// <param name="pointCount">Number of calibration points (3–16).</param>
    /// <param name="screenSize">Screen dimensions in physical pixels.</param>
    /// <returns>The number of target positions in this session.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a session is already in progress.</exception>
    public int StartSession(int pointCount, PhysicalPoint screenSize)
    {
        if (_targetPositions != null)
            throw new InvalidOperationException("Session already in progress.");

        _targetPositions = CalibrationTargetPositionHelper.GetPositions(pointCount, screenSize).ToList();
        _readOnlyTargetPositions = _targetPositions.AsReadOnly();
        _collectedPoints.Clear();
        _currentTargetIndex = 0;
        _screenSize = screenSize;
        return _targetPositions.Count;
    }

    /// <summary>
    /// Every target position for the current session, in order, or empty when no session is running.
    /// </summary>
    /// <remarks>
    /// Exposed so the overlay can show the WHOLE set at once rather than one dot at a time — the user
    /// reported having to search the screen after every advance. It is the same list
    /// <see cref="GetTargetPosition"/> indexes into, deliberately: a view that computed its own copy
    /// from <c>CalibrationTargetPositionHelper</c> could drift from the one being collected against,
    /// and the drawn target would stop being the enforced target.
    /// <para>
    /// A read-only WRAPPER, created once per session rather than per call. Handing out the live
    /// <c>List</c> would let any consumer reorder or clear the very list the collector indexes into,
    /// and rebuilding the wrapper per call would break reference identity — which the view model's
    /// provenance test relies on to prove it publishes THIS list rather than its own recomputation.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PhysicalPoint> TargetPositions =>
        _readOnlyTargetPositions ?? Array.Empty<PhysicalPoint>();

    /// <summary>Returns the target position for the given index in physical screen pixels.</summary>
    /// <param name="index">Zero-based target index.</param>
    /// <returns>Target position in physical screen pixels.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no session is in progress.</exception>
    public PhysicalPoint GetTargetPosition(int index)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");
        return _targetPositions[index];
    }

    /// <summary>Starts collecting gaze samples for the specified target.</summary>
    /// <param name="index">Zero-based target index in the session's target list.</param>
    /// <param name="settleWindowMs">Time in milliseconds to discard samples after gaze arrives on the target.</param>
    /// <param name="maxSamples">Number of post-settle samples to collect for this target.</param>
    /// <exception cref="InvalidOperationException">Thrown when a target is already being collected or no session is active.</exception>
    public void BeginTarget(int index, int settleWindowMs = 0, int maxSamples = int.MaxValue)
    {
        if (_currentCollector != null)
            throw new InvalidOperationException("A target is already being collected. Call EndTarget() first.");

        _currentTargetIndex = index;
        var target = GetTargetPosition(index);
        // One radius, from one place: it gates arrival, gates which samples count, and is what the UI
        // draws. It used to be computed inline here, which is how the drawn target (a 40 px ellipse)
        // and the enforced tolerance (240 px on this display) drifted an order of magnitude apart.
        _currentCollector = new CalibrationPointCollector(
            target, settleWindowMs, maxSamples,
            bandRadiusPx: CalibrationGeometry.BandRadiusPx(_screenSize));
    }

    /// <summary>
    /// Whether the current target has enough post-arrival samples. The session paces itself on
    /// this rather than on a timer — see <see cref="CalibrationPointCollector.IsComplete"/>.
    /// </summary>
    public bool IsTargetComplete => _currentCollector?.IsComplete ?? false;

    /// <summary>Adds a raw gaze sample to the current target's collector. Non-finite or far-off-screen samples are silently dropped.</summary>
    /// <param name="gaze">Raw gaze position in physical screen pixels.</param>
    public void AddRawSample(PhysicalPoint gaze)
    {
        // Drop implausible samples (non-finite / far off-screen) so a misbehaving sensor cannot
        // pollute a target's mean during capture.
        if (!CalibrationValidator.IsPlausibleSample(gaze, _screenSize.X, _screenSize.Y))
            return;
        _currentCollector?.AddRawSample(gaze);
    }

    /// <summary>Whether a target is currently being collected.</summary>
    public bool IsCollecting => _currentCollector?.IsCollecting ?? false;

    /// <summary>
    /// Whether gaze has reached the current target — proximity to it, or steady fixation somewhere.
    /// Read-only passthrough of <see cref="CalibrationPointCollector.HasArrived"/>, exposed so the
    /// scripted ground-truth recorder can mark where the fixation actually starts: the span before
    /// arrival is the saccade and the search, and scoring deviation over it would measure the user
    /// finding the target rather than holding it.
    /// </summary>
    public bool HasArrivedAtTarget => _currentCollector?.HasArrived ?? false;

    /// <summary>
    /// In-band samples collected for the current target so far. Progress is measured in these, not in
    /// elapsed time, so a user who holds the target in several short bursts still makes progress.
    /// </summary>
    public int CurrentTargetInBandSamples => _currentCollector?.InBandSamples ?? 0;

    /// <summary>
    /// Fraction of the current target's post-settle samples that landed in the band, or null before
    /// any have been seen. Surfaced so the UI can show quality while it is still fixable.
    /// </summary>
    public double? CurrentTargetCoverage => _currentCollector?.Coverage;

    /// <summary>
    /// The band radius in force for this session, in physical pixels — what the UI must draw so the
    /// tolerance the user sees is the tolerance being enforced.
    /// </summary>
    public double BandRadiusPx => CalibrationGeometry.BandRadiusPx(_screenSize);

    /// <summary>
    /// Ends collection for the current target and returns the computed <see cref="CalibrationPoint"/>.
    /// </summary>
    /// <returns>The <see cref="CalibrationPoint"/> with mean, standard deviation, and sample count.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no target is being collected or no samples were captured.</exception>
    public CalibrationPoint EndTarget()
    {
        if (_currentCollector == null)
            throw new InvalidOperationException("No target being collected.");
        var point = _currentCollector.End();
        _collectedPoints.Add(point);
        _currentCollector = null;
        return point;
    }

    /// <summary>
    /// Completes the calibration session by fitting a transform from all collected points,
    /// validating the result, and activating the transform.
    /// </summary>
    /// <param name="screenSize">Screen dimensions in physical pixels.</param>
    /// <param name="monitorDeviceName">GDI display name of the monitor (e.g. <c>\\.\DISPLAY1</c>).</param>
    /// <param name="monitorHardwareId">
    /// Stable EDID-derived identity of the monitor (see <see cref="CalibrationData.MonitorHardwareId"/>).
    /// Pass empty when the platform cannot determine one; <see cref="Load"/> then matches on
    /// <paramref name="monitorDeviceName"/> instead.
    /// </param>
    /// <returns>The <see cref="CalibrationData"/> with the fitted transform and metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no session is in progress.</exception>
    /// <exception cref="CalibrationFailedException">
    /// Thrown when the collected data or the fitted transform is refused. The exception carries a
    /// typed <see cref="CalibrationFailure"/>; the calibration window renders the user's sentence
    /// from it.
    /// </exception>
    public CalibrationData CompleteSession(PhysicalPoint screenSize, string monitorDeviceName, string monitorHardwareId = "")
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");

        var points = _collectedPoints.ToArray();

        // Reject capture data where the sensor was not tracking the user's gaze — the gaze barely
        // moved across targets that span the screen. Caught before fitting for a clear message.
        if (!CalibrationValidator.HasSufficientGazeVariation(points))
            throw new CalibrationFailedException(new CalibrationFailure(CalibrationFailureCode.GazeVariation));

        AffineTransform transform;
        try
        {
            transform = AffineTransform.Fit(points);
        }
        catch (InvalidOperationException ex)
        {
            // Fit's own guards (non-finite / singular) are technical and stay as they are. On the
            // calibration surface their words would be raw English that can never be translated, so
            // the fault is carried as Unexpected and the technical message is kept for logs.
            throw new CalibrationFailedException(CalibrationFailure.Unexpected, ex);
        }

        var data = new CalibrationData
        {
            Type = TransformType.Affine,
            TransformParams = transform.GetParameters(),
            Points = points,
            CalibratedAt = DateTimeOffset.UtcNow,
            ScreenWidth = (int)screenSize.X,
            ScreenHeight = (int)screenSize.Y,
            MonitorDeviceName = monitorDeviceName,
            MonitorHardwareId = monitorHardwareId,
        };

        // Reject degenerate fits before they are applied or persisted. A calibration fit to
        // near-constant or contradictory gaze samples can yield a finite (so AffineTransform.Fit's
        // non-finite/singular guards don't catch it) but unusable transform that maps every gaze
        // point far off-screen — silently breaking all gaze interaction. The caller surfaces this
        // as a failed calibration and never persists it.
        if (!CalibrationValidator.IsTransformSane(transform, screenSize.X, screenSize.Y))
            throw new CalibrationFailedException(new CalibrationFailure(CalibrationFailureCode.UnusableTransform));

        // Also reject a fit that stays on-screen but is far from identity (flips/amplifies gaze): it would
        // pass IsTransformSane yet break fixation. See CalibrationValidator.IsNearIdentity.
        if (!CalibrationValidator.IsNearIdentity(transform, screenSize.X, screenSize.Y))
            throw new CalibrationFailedException(new CalibrationFailure(CalibrationFailureCode.DistortedTransform));

        // Residual, precision AND coverage, per target, with the failing targets named.
        //
        // This replaces a single whole-session residual check whose message was "Calibration accuracy
        // is too poor (NNNpx off target). Please recalibrate." Two things were wrong with that. Its
        // threshold admitted 907 px on this display — the 2026-09-12 calibration passed it with
        // 450–500 px residuals and was then applied to every gaze point, which is worse than having no
        // calibration at all. And its message gave the user nothing: they cannot see which point went
        // wrong, so a retry is a blind repeat of the thing that just failed, paid for in the one
        // input channel they have.
        //
        // Residual alone would still not be enough now. It measures whether the transform reproduces
        // the collected MEANS, and a mean built from scattered samples can land exactly on its target
        // while describing nothing the user did — so scatter and coverage are gated independently.
        var failures = CalibrationValidator.FindQualityFailures(transform, points, screenSize);
        if (failures.Count > 0)
            throw new CalibrationFailedException(
                new CalibrationFailure(CalibrationFailureCode.Quality, failures));

        _currentTransform = transform;
        CurrentData = data;
        LastLoadRejection = null;

        _targetPositions = null;
        _readOnlyTargetPositions = null;
        _collectedPoints.Clear();
        return data;
    }

    /// <summary>
    /// Ends the current target without producing a <see cref="CalibrationPoint"/>, discarding whatever
    /// the collector held.
    /// </summary>
    /// <remarks>
    /// Exists for the measure-only probe, where a target the user never got within the band of is not a
    /// session failure — it is one window of evidence, and the five-target decision rule needs the
    /// other windows. The collector itself refuses to produce a point from zero in-band samples (see
    /// <see cref="CalibrationPointCollector.End"/>), so without this the probe would end at the first
    /// empty target exactly when bias is large enough to matter. Nothing is added to the session's
    /// points: this target contributes no fit input, only its sidecar window.
    /// </remarks>
    public void DiscardTarget()
    {
        _currentCollector = null;
    }

    /// <summary>Cancels the current calibration session, discarding all collected data.</summary>
    public void CancelSession()
    {
        _targetPositions = null;
        _readOnlyTargetPositions = null;
        _collectedPoints.Clear();
        _currentCollector = null;
    }

    /// <summary>Clears the current calibration transform and related data.</summary>
    public void Clear()
    {
        _currentTransform = null;
        CurrentData = null;
        // The user deliberately discarded the calibration, so an earlier load rejection is no longer
        // the reason there is none — leaving it set would make the UI blame a monitor change.
        LastLoadRejection = null;
    }

    /// <summary>
    /// Loads calibration data from a JSON file. Validates that the screen size and monitor match,
    /// and that the transform is usable, before applying it. Mismatched or invalid data is ignored
    /// (leaving the pipeline on raw gaze) and every rejection is logged as a warning and recorded
    /// in <see cref="LastLoadRejection"/> for the UI.
    /// </summary>
    /// <param name="filePath">Full path to the calibration JSON file.</param>
    /// <param name="currentScreenSize">Current screen dimensions in physical pixels.</param>
    /// <param name="currentMonitorDeviceName">Current GDI display name (e.g. <c>\\.\DISPLAY1</c>).</param>
    /// <param name="currentMonitorHardwareId">
    /// Current stable EDID-derived monitor identity (see <see cref="CalibrationData.MonitorHardwareId"/>).
    /// When this and the persisted record both carry one, it is what the monitor match is decided on, so
    /// display renumbering no longer discards the calibration. Pass empty to match on the device name.
    /// </param>
    public void Load(
        string filePath,
        PhysicalPoint currentScreenSize,
        string currentMonitorDeviceName,
        string currentMonitorHardwareId = "")
    {
        LastLoadRejection = null;
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<CalibrationData>(json, s_readOptions);
            if (data == null)
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} deserialized to null; ignoring", filePath);
                LastLoadRejection = StringTable.Current.T("settings.calibration.rejected.empty");
                return;
            }

            int currentWidth = (int)currentScreenSize.X;
            int currentHeight = (int)currentScreenSize.Y;

            if (data.ScreenWidth != currentWidth || data.ScreenHeight != currentHeight)
            {
                // Was a bare `return` until 2026-08-02: a discarded calibration left no trace at all,
                // so "why did dwell start drifting?" was unanswerable from the log.
                _logger?.LogWarning(
                    "Calibration from {FilePath} was recorded for a {SavedWidth}x{SavedHeight} screen but the "
                    + "current screen is {CurrentWidth}x{CurrentHeight}; NOT applying it — gaze runs uncalibrated "
                    + "until you recalibrate. Saved on monitor {SavedMonitor} (hardware id {SavedHardwareId}) "
                    + "at {CalibratedAt:u}",
                    filePath, data.ScreenWidth, data.ScreenHeight, currentWidth, currentHeight,
                    Describe(data.MonitorDeviceName), Describe(data.MonitorHardwareId), data.CalibratedAt);
                LastLoadRejection = StringTable.Current.T(
                    "settings.calibration.rejected.screen",
                    data.ScreenWidth, data.ScreenHeight, currentWidth, currentHeight);
                return;
            }

            var match = MatchMonitor(data, currentMonitorDeviceName, currentMonitorHardwareId);
            if (match == MonitorMatch.Mismatch)
            {
                // Same as above: this path used to `return` silently, which is how an inert calibration
                // could survive weeks unnoticed. Name BOTH sides so the log answers the question alone.
                _logger?.LogWarning(
                    "Calibration from {FilePath} was recorded on a different monitor; NOT applying it — gaze "
                    + "runs uncalibrated until you recalibrate. Saved: name={SavedMonitor}, "
                    + "hardwareId={SavedHardwareId}, calibrated {CalibratedAt:u}. Current: name={CurrentMonitor}, "
                    + "hardwareId={CurrentHardwareId}. Note Windows renumbers \\\\.\\DISPLAYn when monitors are "
                    + "attached, detached, or re-enumerated, so a name-only difference can still be the same "
                    + "physical monitor — that case is matched by hardware id once both records carry one",
                    filePath, Describe(data.MonitorDeviceName), Describe(data.MonitorHardwareId),
                    data.CalibratedAt, Describe(currentMonitorDeviceName), Describe(currentMonitorHardwareId));
                // Whole phrases, one per combination of the optional hardware ids. The sentence used to
                // be assembled from a base string plus a conditionally appended ", {id}" fragment,
                // which no translation can carry: German decides its own punctuation and word order,
                // and a fragment is not a phrase. The settings keying (T07) replaces each arm's
                // literal as a whole.
                string savedMonitor = Describe(data.MonitorDeviceName);
                string currentMonitor = Describe(currentMonitorDeviceName);
                bool savedHasHardwareId = !string.IsNullOrEmpty(data.MonitorHardwareId);
                bool currentHasHardwareId = !string.IsNullOrEmpty(currentMonitorHardwareId);
                LastLoadRejection = (savedHasHardwareId, currentHasHardwareId) switch
                {
                    (true, true) =>
                        StringTable.Current.T("settings.calibration.rejected.monitor.both_ids",
                            savedMonitor, data.MonitorHardwareId, currentMonitor, currentMonitorHardwareId),
                    (true, false) =>
                        StringTable.Current.T("settings.calibration.rejected.monitor.saved_id",
                            savedMonitor, data.MonitorHardwareId, currentMonitor),
                    (false, true) =>
                        StringTable.Current.T("settings.calibration.rejected.monitor.current_id",
                            savedMonitor, currentMonitor, currentMonitorHardwareId),
                    (false, false) =>
                        StringTable.Current.T("settings.calibration.rejected.monitor.names_only",
                            savedMonitor, currentMonitor),
                };
                return;
            }

            if (data.TransformParams is not { Length: 6 })
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} has invalid transform parameters (expected 6); ignoring",
                    filePath);
                LastLoadRejection = StringTable.Current.T("settings.calibration.rejected.malformed");
                return;
            }

            var transform = new AffineTransform();
            transform.SetParameters(data.TransformParams);

            // Defense in depth: never trust a persisted transform that maps gaze far off-screen
            // (e.g. a degenerate calibration written by an older build). Ignoring it leaves the
            // pipeline uncalibrated (raw gaze) rather than poisoning every gaze point.
            if (!CalibrationValidator.IsTransformSane(transform, currentScreenSize.X, currentScreenSize.Y))
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} maps gaze off-screen (degenerate transform); ignoring",
                    filePath);
                LastLoadRejection =
                    StringTable.Current.T("settings.calibration.rejected.off_screen");
                return;
            }

            // Defense in depth: also reject a transform that stays on-screen but is far from identity
            // (axis flip / gross scale / large offset). Such a fit passes IsTransformSane yet distorts and
            // amplifies gaze enough to break fixation. EyeCtrl calibration fine-tunes already-screen-space
            // gaze, so a valid transform is near-identity; ignoring a degenerate one leaves the pipeline on
            // raw (tracker-calibrated) gaze rather than poisoning every gaze point.
            if (!CalibrationValidator.IsNearIdentity(transform, currentScreenSize.X, currentScreenSize.Y))
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} deviates too far from identity (degenerate fit); ignoring",
                    filePath);
                LastLoadRejection =
                    StringTable.Current.T("settings.calibration.rejected.flips");
                return;
            }

            _currentTransform = transform;
            CurrentData = data;

            if (match == MonitorMatch.HardwareId &&
                !string.Equals(data.MonitorDeviceName, currentMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                RefreshPersistedDeviceName(filePath, data, currentMonitorDeviceName);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Failed to load calibration data from {FilePath}; ignoring", filePath);
            LastLoadRejection = StringTable.Current.T("settings.calibration.rejected.unreadable");
        }
    }

    /// <summary>How <see cref="Load"/> resolved the persisted monitor against the current one.</summary>
    private enum MonitorMatch
    {
        /// <summary>Both sides carried a stable hardware id and they agree.</summary>
        HardwareId,
        /// <summary>No hardware id to compare, but the GDI display names agree (legacy path).</summary>
        DeviceName,
        /// <summary>Different monitor — the calibration must not be applied.</summary>
        Mismatch,
    }

    private static MonitorMatch MatchMonitor(CalibrationData data, string currentDeviceName, string currentHardwareId)
    {
        // Prefer the stable hardware id. The GDI name (\\.\DISPLAYn) is an enumeration index that Windows
        // reassigns on dock changes, driver updates, and reboots, so matching on it discards a perfectly
        // good calibration for the very same physical monitor.
        if (!string.IsNullOrEmpty(data.MonitorHardwareId) && !string.IsNullOrEmpty(currentHardwareId))
        {
            return string.Equals(data.MonitorHardwareId, currentHardwareId, StringComparison.OrdinalIgnoreCase)
                ? MonitorMatch.HardwareId
                : MonitorMatch.Mismatch;
        }

        // Legacy record, or the platform could not determine an id right now: fall back to the name. This
        // is deliberately strict — a name-only match is the only evidence available, and applying a fit
        // from a genuinely different monitor would reintroduce the silent-drift failure this guards against.
        return string.Equals(data.MonitorDeviceName, currentDeviceName, StringComparison.OrdinalIgnoreCase)
            ? MonitorMatch.DeviceName
            : MonitorMatch.Mismatch;
    }

    /// <summary>
    /// Rewrites the persisted record with the monitor's current GDI name after a hardware-id match proved
    /// it is the same physical display under a new number. Keeps the legacy name-only path accurate for the
    /// day the hardware id cannot be read. Best-effort: a failed rewrite must not disturb the loaded
    /// calibration, which is already applied by this point.
    /// <para>
    /// This is a write during startup, which is the riskiest moment to touch a file the whole gaze
    /// pipeline depends on. It goes through <see cref="Save"/>, so it is atomic: a crash mid-refresh
    /// leaves the previous good <c>calibration.json</c> exactly as it was. The write is kept rather than
    /// deferred to the next real save because there may not be one — a user who never recalibrates would
    /// keep a stale device name forever, and the refresh exists precisely for the day the hardware id
    /// cannot be read and that stale name is all the match has to go on.
    /// </para>
    /// </summary>
    private void RefreshPersistedDeviceName(string filePath, CalibrationData data, string currentMonitorDeviceName)
    {
        _logger?.LogInformation(
            "Calibration monitor matched by hardware id {HardwareId}; the display was renumbered from "
            + "{SavedMonitor} to {CurrentMonitor}. Applying the calibration and refreshing the stored name",
            data.MonitorHardwareId, Describe(data.MonitorDeviceName), Describe(currentMonitorDeviceName));

        var refreshed = new CalibrationData
        {
            Type = data.Type,
            TransformParams = data.TransformParams,
            Points = data.Points,
            CalibratedAt = data.CalibratedAt,
            ScreenWidth = data.ScreenWidth,
            ScreenHeight = data.ScreenHeight,
            MonitorDeviceName = currentMonitorDeviceName,
            MonitorHardwareId = data.MonitorHardwareId,
        };

        try
        {
            Save(filePath, refreshed);
            CurrentData = refreshed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex,
                "Could not refresh the stored monitor name in {FilePath}; the calibration is applied regardless",
                filePath);
        }
    }

    private static string Describe(string? value) =>
        string.IsNullOrEmpty(value) ? StringTable.Current.T("settings.calibration.none") : value;

    /// <summary>
    /// Persists calibration data to disk as JSON, atomically (temp file + rename), mirroring
    /// <see cref="Adaptation.AdaptationStateFile.Save"/> and <see cref="Settings.SettingsService.Save"/>.
    /// <para>
    /// The atomicity matters more here than for any other file EyeCtrl writes: this transform is applied
    /// to EVERY gaze point, so a half-written <c>calibration.json</c> — a crash or power loss mid-write —
    /// does not merely lose a setting, it fails the next load and leaves gaze running uncalibrated with
    /// no obvious cause. Writing elsewhere and renaming means the destination only ever holds the
    /// complete old file or the complete new one. <see cref="Load"/>'s monitor-name refresh writes
    /// through here too, so a crash during startup cannot damage a good calibration.
    /// </para>
    /// </summary>
    /// <param name="filePath">Full path for the output JSON file.</param>
    /// <param name="data">The <see cref="CalibrationData"/> to persist.</param>
    public void Save(string filePath, CalibrationData data)
    {
        var json = JsonSerializer.Serialize(data, s_writeOptions);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }
}
