namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Models;

/// <summary>
/// One raw gaze sample as the session saw it, kept inside its target's window.
/// <para>
/// Stored in the sidecar itself so the sidecar is self-contained: the offline estimator must run from
/// the sidecar alone, without the raw recorder's single rotating file. The recorder is a global
/// opt-in that appends to and rotates one path, so requiring it for a measurement session would make
/// the probe's data depend on a file a later session can overwrite — and, worse, on a hidden
/// precondition the eye-only user cannot satisfy from the settings page.
/// </para>
/// </summary>
/// <param name="X">Raw gaze X in physical screen pixels, exactly as the provider delivered it.</param>
/// <param name="Y">Raw gaze Y in physical screen pixels.</param>
/// <param name="TimestampUs">Device timestamp, the clock the rest of the sidecar is in.</param>
/// <param name="Valid">False for samples the tracker marked invalid (eyes lost). Stored rather than
/// dropped so the window's true sample count and timeline survive; consumers that need positions
/// filter on this.</param>
public readonly record struct GroundTruthSample(double X, double Y, long TimestampUs, bool Valid);

/// <summary>
/// What kind of session produced a sidecar. Recorded so a probe's file can never be mistaken for a
/// calibration's, and vice versa, when the operator comes back to them days later.
/// </summary>
public enum GroundTruthMode
{
    /// <summary>A normal calibration session's sidecar, written to the configured path.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("calibration")]
    Calibration,

    /// <summary>
    /// A measure-only bias probe's sidecar, written to its own timestamped file: the plan is
    /// probe → Tobii recalibration → probe again, and the second probe must not erase the first.
    /// </summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("measure-only")]
    MeasureOnly,
}

/// <summary>
/// How a scripted session ended. The measure-only probe persists the sidecar on EVERY outcome, so the
/// outcome is what lets later analysis tell a complete five-target run from one the user abandoned or
/// the tracker cut short — without that field, a partial window set and a full one look identical.
/// </summary>
public enum GroundTruthOutcome
{
    /// <summary>Every target ran to its end. Also what a schema-1 sidecar without the field means.</summary>
    Completed,

    /// <summary>The session ran to its end, but at least one target collected nothing (measure-only).</summary>
    Partial,

    /// <summary>The user stopped the session; whatever windows existed at that moment were kept.</summary>
    Abandoned,

    /// <summary>The session ended on a fault rather than on a target or the user.</summary>
    Failed,
}

/// <summary>
/// One scripted target: where the user was asked to look, and the device-time window during which
/// they were asked to hold it.
/// <para>
/// Every timestamp here comes from <see cref="GazePoint.TimestampMicroseconds"/> — the SAME clock the
/// raw recording is written in — never from a wall clock. That is what makes the sidecar alignable to
/// the recording at all: no conversion, no drift, no assumption that the two were sampled together.
/// </para>
/// </summary>
/// <param name="TargetId">Zero-based index in the session's target order.</param>
/// <param name="X">Target X in physical screen pixels — the ground-truth position.</param>
/// <param name="Y">Target Y in physical screen pixels.</param>
/// <param name="DisplayedFromUs">Device timestamp of the first sample seen while this target was shown.</param>
/// <param name="ArrivedAtUs">
/// Device timestamp at which the collector first reported the user had ARRIVED (gaze near the target,
/// or steadily fixating somewhere). Null when they never arrived. Scoring should start here rather
/// than at <paramref name="DisplayedFromUs"/>: the span before arrival is the saccade and the search,
/// not the fixation being measured.
/// </param>
/// <param name="EndedAtUs">Device timestamp of the last sample seen while this target was shown.</param>
/// <param name="SampleCount">Samples observed across the whole displayed window.</param>
public readonly record struct GroundTruthWindow(
    int TargetId,
    double X,
    double Y,
    long DisplayedFromUs,
    long? ArrivedAtUs,
    long EndedAtUs,
    int SampleCount)
{
    /// <summary>
    /// The raw samples seen while this target was shown, in arrival order — what makes the sidecar
    /// self-contained. Empty for a schema-1 sidecar written before this field existed; the analyzer
    /// then needs the recording, which is the old contract.
    /// </summary>
    /// <remarks>
    /// Backed by a nullable field rather than a plain auto-property because the member is optional on
    /// read: <see cref="System.Text.Json"/> leaves an absent property at its default, and for a struct
    /// that default is null regardless of an initializer — the reader's "a schema-1 sidecar still
    /// parses" guarantee would otherwise hand consumers a null list to enumerate.
    /// </remarks>
    public IReadOnlyList<GroundTruthSample> Samples
    {
        get => _samples ?? Array.Empty<GroundTruthSample>();
        init => _samples = value;
    }

    private readonly IReadOnlyList<GroundTruthSample>? _samples;

    /// <summary>The target position as a point.</summary>
    public PhysicalPoint Target => new(X, Y);

    /// <summary>
    /// The window to score over: from arrival (or from first display, if arrival was never detected)
    /// to the end.
    /// </summary>
    public (long FromUs, long ToUs) ScoringWindow => (ArrivedAtUs ?? DisplayedFromUs, EndedAtUs);

    /// <summary>Whether <paramref name="timestampUs"/> falls inside <see cref="ScoringWindow"/>, inclusive.</summary>
    /// <param name="timestampUs">A device timestamp from the recorded stream.</param>
    public bool Contains(long timestampUs)
    {
        var (from, to) = ScoringWindow;
        return timestampUs >= from && timestampUs <= to;
    }
}

/// <summary>
/// A scripted ground-truth session: the sidecar written alongside a raw recording so the analyzer can
/// score ABSOLUTE deviation — distance from where the user was actually asked to look — instead of
/// deviation from the recording's own centroid, which cannot see a systematic offset at all.
/// </summary>
/// <param name="SchemaVersion">Bumped when the shape changes, so a reader can refuse what it cannot parse.</param>
/// <param name="ProviderType">Gaze provider identity for the session, e.g. <c>StreamEngine</c>.</param>
/// <param name="ScreenWidthPhysical">Screen width in physical pixels at run time.</param>
/// <param name="ScreenHeightPhysical">Screen height in physical pixels.</param>
/// <param name="SettleWindowMs">The collector's settle window for this run, recorded for interpretation.</param>
/// <param name="Windows">One entry per target, in the order presented.</param>
public sealed record GroundTruthSession(
    int SchemaVersion,
    string ProviderType,
    double ScreenWidthPhysical,
    double ScreenHeightPhysical,
    int SettleWindowMs,
    IReadOnlyList<GroundTruthWindow> Windows)
{
    /// <summary>The schema this build writes.</summary>
    /// <remarks>
    /// Kept at 1 while <see cref="Outcome"/> and <see cref="GroundTruthWindow.Samples"/> were added:
    /// both are additive JSON members that a schema-1 reader ignores, and a reader that predates them
    /// still gets a well-formed session. Bumping would instead reject every sidecar already on disk,
    /// which is exactly the kind of data loss this ticket exists to stop.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// How the session ended. Defaults to <see cref="GroundTruthOutcome.Completed"/> — the only
    /// outcome a schema-1 sidecar could describe, since before the measure-only probe a sidecar was
    /// written on success alone.
    /// </summary>
    public GroundTruthOutcome Outcome { get; init; } = GroundTruthOutcome.Completed;

    /// <summary>
    /// What kind of session this is. Defaults to <see cref="GroundTruthMode.Calibration"/> — what a
    /// schema-1 sidecar was.
    /// </summary>
    public GroundTruthMode Mode { get; init; } = GroundTruthMode.Calibration;
}

/// <summary>
/// Observes a scripted collection run and records its ground truth. Implemented by
/// <see cref="ScriptedGroundTruthRecorder"/>; the calibration view model calls it when one is attached
/// and is otherwise unchanged.
/// </summary>
public interface IGroundTruthSink
{
    /// <summary>Called when a scripted session begins, before any target is shown.</summary>
    /// <param name="providerType">Gaze provider identity.</param>
    /// <param name="screenSize">Screen dimensions in physical pixels.</param>
    /// <param name="settleWindowMs">The collector's settle window for this run.</param>
    /// <param name="mode">
    /// What kind of session this is. A measure-only probe gets its own timestamped file; a normal
    /// calibration keeps the configured path.
    /// </param>
    void BeginSession(
        string providerType, PhysicalPoint screenSize, int settleWindowMs,
        GroundTruthMode mode = GroundTruthMode.Calibration);

    /// <summary>
    /// Called for each raw gaze sample while a target is displayed. Must be cheap: this runs on the
    /// gaze thread, in the same callback that feeds the calibration collector.
    /// </summary>
    /// <param name="targetId">Zero-based index of the target currently shown.</param>
    /// <param name="target">Its position in physical screen pixels.</param>
    /// <param name="sample">
    /// The sample itself, raw and unmodified, in the device clock — so the sidecar can carry the
    /// stream that produced the window and the estimator runs without the separate recorder file.
    /// Invalid samples are passed too: they carry no position but they do carry the timeline.
    /// </param>
    /// <param name="hasArrived">Whether the collector considers the user to have arrived at the target.</param>
    void OnSample(int targetId, PhysicalPoint target, GazePoint sample, bool hasArrived);

    /// <summary>
    /// Called when the session finishes, persisting the sidecar with the given outcome. The
    /// measure-only probe calls this on success, partial data, abandonment and failure alike; a normal
    /// calibration calls it only on success and discards with <see cref="AbandonSession"/> otherwise.
    /// </summary>
    /// <param name="outcome">How the session ended; recorded in the sidecar.</param>
    void CompleteSession(GroundTruthOutcome outcome = GroundTruthOutcome.Completed);

    /// <summary>Called when the session is cancelled or fails; discards what was gathered.</summary>
    void AbandonSession();
}
