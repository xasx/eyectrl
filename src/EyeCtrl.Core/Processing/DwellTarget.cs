namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// A target area for dwell-based gaze interactions. All coordinates are physical screen pixels.
/// Circular by default; rectangular when <see cref="Bounds"/> is set.
/// </summary>
public class DwellTarget
{
    /// <summary>Unique identifier for this dwell target.</summary>
    public string Id { get; }
    /// <summary>Center position in physical screen pixels (used for nearest-target selection).</summary>
    public PhysicalPoint Center { get; set; }
    /// <summary>Hit-test radius in physical pixels (used when <see cref="Bounds"/> is null).</summary>
    public double Radius { get; internal set; }
    /// <summary>Required dwell time in microseconds for activation.</summary>
    public long DwellTimeMicroseconds { get; internal set; }
    /// <summary>Timestamp in microseconds when gaze first entered this target, or null.</summary>
    public long? EnteredTimestamp { get; set; }
    /// <summary>Whether gaze is currently inside this target's hit area.</summary>
    public bool IsGazeInside { get; set; }

    /// <summary>
    /// Timestamp in microseconds of the last <c>Update</c> at which this target was the nearest
    /// hit — the last moment the gaze was known to be inside. Only read by a detector with a
    /// pause grace (SP-4c D4): it is the anchor of the "left at" window, so a paused target's
    /// accumulated dwell is dropped once the absence exceeds the grace. Unused (and never read)
    /// when the owning detector's grace is 0, which is every caller but the sub-pixel probe.
    /// </summary>
    internal long? LastInsideTimestampUs { get; set; }

    /// <summary>
    /// Whether the owning detector last reported this target as paused (gaze left mid-dwell and the
    /// grace still holds its progress) through <c>DwellDetector.TargetPauseChanged</c>. Display
    /// bookkeeping only — it makes the paused/resumed event fire exactly on a change, and no hold
    /// decision reads it.
    /// </summary>
    internal bool GracePaused { get; set; }

    /// <summary>
    /// Opts this target out of the detector's pause grace: it behaves as on a grace-0 detector
    /// (reset on leave, restart on a silence at the clamp). For a target whose confirm flow depends
    /// on a fresh dwell after a leave — the toolbar's Reset-adaptation button, whose preview must be
    /// followed by a pass from zero. Default false; meaningless at grace 0.
    /// </summary>
    public bool PauseGraceExempt { get; set; }

    /// <summary>Optional rectangular bounds. When set, HitTest uses rect containment; null ⇒ circle.</summary>
    public PhysicalRect? Bounds { get; set; }

    // As-registered hit-area extents, captured at construction. ApplyHitAreaMargin derives the live
    // geometry from these rather than from the current values, so repeated calls with the same margin
    // are idempotent instead of compounding — Phase 1b recomputes and re-applies a margin every tick.
    private readonly double _baseRadius;
    private double _baseHalfWidth;
    private double _baseHalfHeight;
    private readonly long _baseDwellTimeMicroseconds;

    /// <summary>
    /// Initializes a new circular dwell target.
    /// </summary>
    /// <param name="id">Unique identifier for this target.</param>
    /// <param name="center">Center position in physical screen pixels.</param>
    /// <param name="radius">Hit-test radius in physical pixels.</param>
    /// <param name="dwellTimeMs">Required dwell time in milliseconds.</param>
    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = null;
        _baseRadius = radius;
        _baseDwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Initializes a new rectangular dwell target.
    /// </summary>
    /// <param name="id">Unique identifier for this target.</param>
    /// <param name="bounds">Rectangular bounds in physical screen pixels.</param>
    /// <param name="dwellTimeMs">Required dwell time in milliseconds.</param>
    public DwellTarget(string id, PhysicalRect bounds, int dwellTimeMs)
    {
        Id = id;
        Center = bounds.Center;
        Radius = 0;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = bounds;
        _baseHalfWidth = bounds.Width / 2;
        _baseHalfHeight = bounds.Height / 2;
        _baseDwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Grows (or, for a negative margin, shrinks) the hit area by <paramref name="marginPx"/> physical
    /// pixels on every side, relative to the as-registered extents. Circular targets get
    /// <c>baseRadius + margin</c>; rectangular targets get their base half-extents outset by the margin
    /// around the current <see cref="Center"/> — <see cref="HitTest"/> ignores <see cref="Radius"/>
    /// whenever <see cref="Bounds"/> is set, so a radius-only adjustment would silently do nothing there.
    /// Clamped so the hit area never inverts. Does not touch dwell accumulation state.
    /// </summary>
    /// <param name="marginPx">Signed margin in physical pixels; 0 restores the as-registered geometry.</param>
    internal void ApplyHitAreaMargin(double marginPx)
    {
        if (Bounds is not null)
        {
            double halfW = Math.Max(0, _baseHalfWidth + marginPx);
            double halfH = Math.Max(0, _baseHalfHeight + marginPx);
            Bounds = new PhysicalRect(Center.X - halfW, Center.Y - halfH, halfW * 2, halfH * 2);
        }
        else
        {
            Radius = Math.Max(0, _baseRadius + marginPx);
        }
    }

    /// <summary>The as-registered dwell time in ms — the composition baseline for adaptation routing,
    /// so repeated effective-value applications never compound.</summary>
    internal int BaseDwellMs => (int)(_baseDwellTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
    /// <summary>Sets the effective dwell time (adaptation routing) WITHOUT touching dwell accumulation
    /// state: the next <see cref="DwellDetector.Update(PhysicalPoint, long)"/> re-evaluates elapsed
    /// against the new threshold (design §5/§7.3 — shortening mid-dwell preserves progress and can
    /// activate on the next sample; lengthening keeps progress). Idempotent by construction: callers
    /// derive the value from <see cref="BaseDwellMs"/>, never from the current effective value.</summary>
    internal void ApplyEffectiveDwellMs(int effectiveMs) =>
        DwellTimeMicroseconds = effectiveMs * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>The as-registered reference hit extent the geometry map composes against: the radius
    /// for circular targets, the smaller half-extent for rects — gaze error is isotropic, so the
    /// routing margin is uniform on every side and the narrow dimension (the binding constraint)
    /// sets the scale. Unlike the live <see cref="Radius"/>/<see cref="Bounds"/>, this never changes,
    /// so repeated effective-geometry applications never compound.</summary>
    internal double BaseHitExtent => Bounds is not null
        ? Math.Min(_baseHalfWidth, _baseHalfHeight)
        : _baseRadius;

    /// <summary>
    /// Rebases the as-registered rect extents to <paramref name="rect"/> (an SDK-pushed
    /// size/position change on a live target — ExternalDwellService's UpdateMessage path) and
    /// resets the live geometry to the unmargined rect. The owner must re-apply its stored
    /// geometry map afterwards (<see cref="DwellDetector.RebaseTarget"/> does both), so a routed
    /// margin rides the NEW base instead of being lost — and a later map application can never
    /// revert the new size to stale base extents. Rect targets only — a circular target is a no-op
    /// (circles have no external geometry source; assigning <see cref="Bounds"/> would flip one
    /// to rect hit semantics with no way back, its readonly <c>_baseRadius</c> orphaned);
    /// preserves dwell accumulation state.
    /// </summary>
    internal void Rebase(PhysicalRect rect)
    {
        if (Bounds is null)
            return;
        _baseHalfWidth = rect.Width / 2;
        _baseHalfHeight = rect.Height / 2;
        Center = rect.Center;
        Bounds = rect;
    }

    /// <summary>Rect containment when <see cref="Bounds"/> is set; otherwise circle distance.</summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;

        return point.DistanceSquaredTo(Center) <= Radius * Radius;
    }
}
