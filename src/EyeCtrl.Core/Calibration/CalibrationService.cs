namespace EyeCtrl.Core.Calibration;

using System.Text.Json;
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
        _collectedPoints.Clear();
        _currentTargetIndex = 0;
        _screenSize = screenSize;
        return _targetPositions.Count;
    }

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
    /// <param name="settleWindowMs">Time in milliseconds to discard initial settling samples.</param>
    /// <param name="maxSamples">Maximum number of samples to collect per target.</param>
    /// <exception cref="InvalidOperationException">Thrown when a target is already being collected or no session is active.</exception>
    public void BeginTarget(int index, int settleWindowMs = 0, int maxSamples = int.MaxValue)
    {
        if (_currentCollector != null)
            throw new InvalidOperationException("A target is already being collected. Call EndTarget() first.");

        _currentTargetIndex = index;
        var target = GetTargetPosition(index);
        _currentCollector = new CalibrationPointCollector(target, settleWindowMs, maxSamples);
    }

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
    /// <param name="monitorDeviceName">Name of the monitor for calibration metadata.</param>
    /// <returns>The <see cref="CalibrationData"/> with the fitted transform and metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no session is in progress or the fit fails validation.</exception>
    public CalibrationData CompleteSession(PhysicalPoint screenSize, string monitorDeviceName)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");

        var points = _collectedPoints.ToArray();

        // Reject capture data where the sensor was not tracking the user's gaze — the gaze barely
        // moved across targets that span the screen. Caught before fitting for a clear message.
        if (!CalibrationValidator.HasSufficientGazeVariation(points))
            throw new InvalidOperationException(
                "Gaze barely changed across calibration targets — the eye tracker may not be tracking. Please recalibrate.");

        var transform = AffineTransform.Fit(points);

        var data = new CalibrationData
        {
            Type = TransformType.Affine,
            TransformParams = transform.GetParameters(),
            Points = points,
            CalibratedAt = DateTimeOffset.UtcNow,
            ScreenWidth = (int)screenSize.X,
            ScreenHeight = (int)screenSize.Y,
            MonitorDeviceName = monitorDeviceName,
        };

        // Reject degenerate fits before they are applied or persisted. A calibration fit to
        // near-constant or contradictory gaze samples can yield a finite (so AffineTransform.Fit's
        // non-finite/singular guards don't catch it) but unusable transform that maps every gaze
        // point far off-screen — silently breaking all gaze interaction. The caller surfaces this
        // as a failed calibration and never persists it.
        if (!CalibrationValidator.IsTransformSane(transform, screenSize.X, screenSize.Y))
            throw new InvalidOperationException(
                "Calibration produced an unusable transform — gaze would map off-screen. Please recalibrate.");

        // Also reject a fit that stays on-screen but is far from identity (flips/amplifies gaze): it would
        // pass IsTransformSane yet break fixation. See CalibrationValidator.IsNearIdentity.
        if (!CalibrationValidator.IsNearIdentity(transform, screenSize.X, screenSize.Y))
            throw new InvalidOperationException(
                "Calibration produced a distorted transform (gaze would be flipped or amplified). Please recalibrate.");

        double diagonal = Math.Sqrt(screenSize.X * screenSize.X + screenSize.Y * screenSize.Y);
        double maxResidual = CalibrationValidator.MaxResidualPixels(transform, points);
        if (maxResidual > diagonal * CalibrationValidator.MaxResidualScreenFraction)
            throw new InvalidOperationException(
                $"Calibration accuracy is too poor ({maxResidual:F0}px off target). Please recalibrate.");

        _currentTransform = transform;
        CurrentData = data;

        _targetPositions = null;
        _collectedPoints.Clear();
        return data;
    }

    /// <summary>Cancels the current calibration session, discarding all collected data.</summary>
    public void CancelSession()
    {
        _targetPositions = null;
        _collectedPoints.Clear();
        _currentCollector = null;
    }

    /// <summary>Clears the current calibration transform and related data.</summary>
    public void Clear()
    {
        _currentTransform = null;
        CurrentData = null;
    }

    /// <summary>
    /// Loads calibration data from a JSON file. Validates that the screen size and monitor
    /// match before applying the transform. Silently ignores invalid or mismatched data.
    /// </summary>
    /// <param name="filePath">Full path to the calibration JSON file.</param>
    /// <param name="currentScreenSize">Current screen dimensions in physical pixels.</param>
    /// <param name="currentMonitorDeviceName">Current monitor name for matching against persisted data.</param>
    public void Load(string filePath, PhysicalPoint currentScreenSize, string currentMonitorDeviceName)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<CalibrationData>(json, s_readOptions);
            if (data == null) return;

            if (data.ScreenWidth != (int)currentScreenSize.X ||
                data.ScreenHeight != (int)currentScreenSize.Y ||
                !string.Equals(data.MonitorDeviceName, currentMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (data.TransformParams is not { Length: 6 })
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} has invalid transform parameters (expected 6); ignoring",
                    filePath);
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
                return;
            }

            _currentTransform = transform;
            CurrentData = data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Failed to load calibration data from {FilePath}; ignoring", filePath);
        }
    }

    /// <summary>Persists calibration data to disk as JSON.</summary>
    /// <param name="filePath">Full path for the output JSON file.</param>
    /// <param name="data">The <see cref="CalibrationData"/> to persist.</param>
    public void Save(string filePath, CalibrationData data)
    {
        var json = JsonSerializer.Serialize(data, s_writeOptions);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, json);
    }
}
