namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Models;

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
    public const int CurrentSchemaVersion = 1;
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
    void BeginSession(string providerType, PhysicalPoint screenSize, int settleWindowMs);

    /// <summary>
    /// Called for each raw gaze sample while a target is displayed. Must be cheap: this runs on the
    /// gaze thread, in the same callback that feeds the calibration collector.
    /// </summary>
    /// <param name="targetId">Zero-based index of the target currently shown.</param>
    /// <param name="target">Its position in physical screen pixels.</param>
    /// <param name="deviceTimestampUs">The sample's device timestamp.</param>
    /// <param name="hasArrived">Whether the collector considers the user to have arrived at the target.</param>
    void OnSample(int targetId, PhysicalPoint target, long deviceTimestampUs, bool hasArrived);

    /// <summary>Called when the session finishes successfully; persists the sidecar.</summary>
    void CompleteSession();

    /// <summary>Called when the session is cancelled or fails; discards what was gathered.</summary>
    void AbandonSession();
}
