namespace EyeCtrl.Core.Services;

/// <summary>
/// Provides DPI scale factor and coordinate conversion between
/// physical screen pixels and WPF device-independent pixels (DIPs).
/// Initialize() must be called once at startup before any conversions.
/// All conversion methods are thread-safe after initialization.
/// </summary>
public static class DpiHelper
{
    /// <summary>
    /// The two factors as one immutable value, swapped by a single reference write.
    /// </summary>
    /// <remarks>
    /// These were two independent auto-properties, which was safe only because nothing ever wrote
    /// them after startup — the class doc still says "call once", and that was the whole basis of
    /// its thread-safety claim. Refreshing on a display change breaks that basis: the write now
    /// happens at an arbitrary time while readers are live. Holding the pair in one record and
    /// publishing it by reference restores the guarantee, because a reference write is atomic
    /// where two double writes are not.
    /// </remarks>
    private sealed record Scale(double X, double Y);

    private static volatile Scale _scale = new(1.0, 1.0);

    /// <summary>Horizontal scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleX => _scale.X;

    /// <summary>Vertical scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleY => _scale.Y;

    /// <summary>
    /// Set the DPI scale factors at startup.
    /// From WPF: var m = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
    ///           DpiHelper.Initialize(m.M11, m.M22);
    /// </summary>
    public static void Initialize(double scaleX, double scaleY) => _scale = new Scale(scaleX, scaleY);

    /// <summary>
    /// Re-publishes the scale after a display or scaling change. Same effect as
    /// <see cref="Initialize"/>; named separately because the call sites mean different things and
    /// the startup one is documented as happening exactly once.
    /// </summary>
    /// <remarks>
    /// One honest limit: each read sees a whole, untorn factor, but a caller that reads
    /// <see cref="ScaleX"/> and <see cref="ScaleY"/> as two statements could straddle an update and
    /// mix the axes. Forcing pair-atomicity would mean changing all ~124 call sites to read a
    /// snapshot, and the payoff is near zero — Windows scales both axes together, so the two
    /// factors are equal in every configuration this app runs on, and the window is a few
    /// instructions during a mode switch. Recorded rather than silently accepted.
    /// </remarks>
    public static void Update(double scaleX, double scaleY) => _scale = new Scale(scaleX, scaleY);

    // --- Physical screen pixels → WPF DIPs ---

    /// <summary>Converts a physical X coordinate to logical DIPs.</summary>
    /// <param name="physicalX">X coordinate in physical screen pixels.</param>
    /// <returns>X coordinate in WPF device-independent pixels.</returns>
    public static double PhysicalToLogicalX(double physicalX) => physicalX / ScaleX;
    /// <summary>Converts a physical Y coordinate to logical DIPs.</summary>
    /// <param name="physicalY">Y coordinate in physical screen pixels.</param>
    /// <returns>Y coordinate in WPF device-independent pixels.</returns>
    public static double PhysicalToLogicalY(double physicalY) => physicalY / ScaleY;

    // --- WPF DIPs → Physical screen pixels ---

    /// <summary>Converts a logical X coordinate to physical screen pixels.</summary>
    /// <param name="logicalX">X coordinate in WPF device-independent pixels.</param>
    /// <returns>X coordinate in physical screen pixels.</returns>
    public static double LogicalToPhysicalX(double logicalX) => logicalX * ScaleX;
    /// <summary>Converts a logical Y coordinate to physical screen pixels.</summary>
    /// <param name="logicalY">Y coordinate in WPF device-independent pixels.</param>
    /// <returns>Y coordinate in physical screen pixels.</returns>
    public static double LogicalToPhysicalY(double logicalY) => logicalY * ScaleY;
}
