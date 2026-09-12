namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// An <see cref="IGazeProvider"/> that wraps another one. Exists so code that must reason about the
/// REAL provider — <c>GazeProviderFactory.GetProviderType</c>, which the toolbar uses to show which
/// engine is active — can unwrap without knowing any particular decorator: a wrapped Tobii provider
/// would otherwise be reported as Mouse, telling the user their eye tracker is not in use.
/// </summary>
public interface IGazeProviderDecorator : IGazeProvider
{
    /// <summary>The wrapped provider. Never null; may itself be a decorator.</summary>
    IGazeProvider Inner { get; }
}

/// <summary>
/// The side-channel a recording needs to be replayable: everything about the session that is NOT a
/// gaze sample, captured once at the head of the file.
/// <para>
/// Replay reconstructs <c>isInGap</c> and the filtered point from raw samples alone, but only if it
/// knows every input the live pipeline had — hence the screen size (raw samples are in tracker space
/// and the provider scales by it), the calibration transform's own coefficients (it is applied to
/// every valid sample BEFORE the filter, so replaying without it measures a different signal from
/// the one the user saw) and a settings version the analyzer can join adaptation changes against.
/// </para>
/// <para>
/// Privacy: this is the complete list of context ever written. No window titles, no target ids, no
/// application names — a recording says where its owner looked and when, and nothing about what they
/// were looking at.
/// </para>
/// </summary>
/// <param name="ProviderType">Which gaze provider produced the samples, e.g. <c>StreamEngine</c>.</param>
/// <param name="ScreenWidthPhysical">Screen width in physical pixels at record time.</param>
/// <param name="ScreenHeightPhysical">Screen height in physical pixels at record time.</param>
/// <param name="CalibrationActive">Whether an EyeCtrl calibration transform was applied during the session.</param>
/// <param name="CalibrationVersion">
/// Identifies WHICH calibration was active — the persisted calibration's UTC timestamp in ISO-8601,
/// or empty when none. Two recordings with different values are not comparable.
/// </param>
/// <param name="SettingsVersion">
/// Monotonic counter bumped whenever the settings or adaptation state that shapes the pipeline
/// changes, so the analyzer can join a recording against the configuration in force.
/// </param>
/// <param name="CalibrationTransform">
/// The coefficients of the transform that was applied to every valid sample, or <c>null</c> when none
/// was active. Naming WHICH calibration was in force is not enough for replay: the analyzer has the
/// raw samples, so to see what the pipeline actually saw it must be able to reapply the same mapping,
/// and a recording's <c>calibration.json</c> may have been overwritten by the time it is analyzed.
/// Null while <see cref="CalibrationActive"/> is true means a transform type this recorder does not
/// know how to serialize — the analyzer must refuse rather than replay the raw stream as if it were
/// corrected.
/// </param>
public readonly record struct GazeRecordingHeader(
    string ProviderType,
    double ScreenWidthPhysical,
    double ScreenHeightPhysical,
    bool CalibrationActive,
    string CalibrationVersion,
    long SettingsVersion,
    AffineTransformParameters? CalibrationTransform = null);

/// <summary>
/// The six coefficients of an affine calibration transform, as
/// <c>x' = A*x + B*y + C</c>, <c>y' = D*x + E*y + F</c> — the same ordering
/// <c>AffineTransform.GetParameters</c> returns.
/// <para>
/// Named fields rather than the raw <c>double[]</c> the transform persists, because this ends up in a
/// JSON header a human reads while deciding whether two recordings are comparable, and because an
/// array member would give the header reference equality where it needs value equality.
/// </para>
/// </summary>
public readonly record struct AffineTransformParameters(
    double A, double B, double C, double D, double E, double F)
{
    /// <summary>
    /// Describes a live calibration transform for the recording header, or <c>null</c> when there is
    /// none — or when it is of a type this recorder cannot serialize.
    /// <para>
    /// Separated from the call site and made total (no throw, no partial answer) because the two null
    /// cases mean opposite things downstream. "No calibration was active" is a fully described session;
    /// "a calibration was active but is not representable here" is a session the analyzer must refuse,
    /// and it is <see cref="GazeRecordingHeader.CalibrationActive"/> that tells them apart. A helper
    /// that conflated the two would make an unreplayable recording look like an uncalibrated one.
    /// </para>
    /// </summary>
    /// <param name="transform">The transform in force, typically <c>CalibrationService.CurrentTransform</c>.</param>
    public static AffineTransformParameters? TryDescribe(ICalibrationTransform? transform)
    {
        // Type-checked rather than trusting GetParameters' length: the interface promises an array
        // "suitable for persistence" whose meaning is defined by the implementing type, so six doubles
        // from some future non-affine transform would deserialize into these fields and replay would
        // apply them as if they were an affine map.
        if (transform is not AffineTransform affine) return null;

        var p = affine.GetParameters();
        if (p is not { Length: 6 }) return null;

        return new AffineTransformParameters(p[0], p[1], p[2], p[3], p[4], p[5]);
    }
}

/// <summary>
/// What can only be known once a recording is closed: how much of it was lost.
/// <para>
/// It is a TRAILER, not a header field, for the obvious reason — the drop count at the moment the
/// header is written is always zero. The consequence is the useful part: a recording whose process
/// died, or whose writer faulted mid-session, has no trailer at all, and the analyzer treats a missing
/// trailer exactly as it treats a non-zero drop count. Both mean "this file is not a complete record
/// of the session", and silently measuring either one produces a number that looks like a
/// measurement.
/// </para>
/// </summary>
/// <param name="DroppedSamples">Samples overwritten in the ring before the writer persisted them.</param>
/// <param name="RecordedSamples">Samples handed to the sink over the session, dropped ones included.</param>
public readonly record struct GazeRecordingTrailer(long DroppedSamples, long RecordedSamples);

/// <summary>
/// Destination for recorded raw samples. Implementations are called from the gaze thread and MUST
/// NOT throw, block, or allocate per sample — see <see cref="GazeRecorder"/> for the shipped one.
/// </summary>
public interface IGazeRecordingSink : IDisposable
{
    /// <summary>Records one raw sample. Called on the gaze thread; must be non-blocking and must not throw.</summary>
    /// <param name="sample">The raw sample exactly as the provider produced it.</param>
    void Record(in GazePoint sample);

    /// <summary>
    /// Asks the sink to persist what it holds, without waiting for it. Called from
    /// <see cref="IGazeProvider.Stop"/>, which must not be delayed.
    /// </summary>
    void RequestFlush();

    /// <summary><c>false</c> once the sink has self-disabled after a fault; it then does nothing.</summary>
    bool IsRecording { get; }
}
