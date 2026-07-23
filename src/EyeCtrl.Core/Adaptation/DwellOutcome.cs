namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;

/// <summary>
/// A completed dwell interaction outcome (activation or abandonment), emitted by dwell surfaces
/// for struggle detection. A value type so it is allocation-free on the gaze hot path (design §9.1).
/// </summary>
/// <param name="TimestampMicroseconds">Timestamp of the outcome, in microseconds.</param>
/// <param name="Context">The interaction surface the dwell occurred on.</param>
/// <param name="TargetId">Identifier of the dwell target.</param>
/// <param name="Kind">Whether the dwell activated or was abandoned.</param>
/// <param name="Reason">Why an abandonment occurred; <see cref="AbandonReason.None"/> for activations.</param>
/// <param name="ProgressAtEnd">Dwell fraction [0..1] reached at the outcome (1.0 for activations).</param>
/// <param name="GazePoint">Gaze position at the outcome, in physical screen pixels.</param>
/// <param name="TargetCenter">Target center, in physical screen pixels.</param>
/// <param name="TargetRadius">Circular target hit radius in physical pixels; 0 for rectangular targets.</param>
/// <param name="TargetBounds">Rectangular target bounds when the target is a rect; null for circular targets.</param>
public readonly record struct DwellOutcome(
    long TimestampMicroseconds,
    DwellContext Context,
    string TargetId,
    OutcomeKind Kind,
    AbandonReason Reason,
    double ProgressAtEnd,
    PhysicalPoint GazePoint,
    PhysicalPoint TargetCenter,
    double TargetRadius,
    PhysicalRect? TargetBounds)
{
    /// <summary>Horizontal near-miss (gaze − target center) in physical pixels.</summary>
    public double NearMissX => GazePoint.X - TargetCenter.X;

    /// <summary>Vertical near-miss (gaze − target center) in physical pixels.</summary>
    public double NearMissY => GazePoint.Y - TargetCenter.Y;
}
