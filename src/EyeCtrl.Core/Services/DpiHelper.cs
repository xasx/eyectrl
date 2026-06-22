namespace EyeCtrl.Core.Services;

/// <summary>
/// Provides DPI scale factor and coordinate conversion between
/// physical screen pixels and WPF device-independent pixels (DIPs).
/// Initialize() must be called once at startup before any conversions.
/// All conversion methods are thread-safe after initialization.
/// </summary>
public static class DpiHelper
{
    /// <summary>Horizontal scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleX { get; private set; } = 1.0;

    /// <summary>Vertical scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleY { get; private set; } = 1.0;

    /// <summary>
    /// Set the DPI scale factors. Call once at app startup.
    /// From WPF: var m = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
    ///           DpiHelper.Initialize(m.M11, m.M22);
    /// </summary>
    public static void Initialize(double scaleX, double scaleY)
    {
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

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
