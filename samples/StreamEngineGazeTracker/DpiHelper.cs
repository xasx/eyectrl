namespace StreamEngineGazeTracker;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Sample-local stand-in for <c>EyeCtrl.Core.Services.DpiHelper</c>.
/// The sample is intentionally self-contained, so the main app's
/// <c>DpiHelper</c> cannot be referenced. This class exposes the same
/// two operations needed here: query the current DPI scale of a
/// visual's window, and convert physical screen pixels to WPF DIPs.
/// Re-query per-tick to handle changes if/when the sample opts into
/// PerMonitorV2 awareness in the future.
/// </summary>
internal static class DpiHelper
{
    /// <summary>
    /// Returns the horizontal DPI scale of the given visual's window
    /// (e.g. 2.0 at 200% scaling). Falls back to 1.0 if the visual
    /// is not yet connected to a <see cref="PresentationSource"/>.
    /// </summary>
    public static double GetScale(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>
    /// Returns the horizontal and vertical DPI scales of the given
    /// visual's window in a single lookup (e.g. (2.0, 2.0) at 200%
    /// scaling). Windows scaling is uniform in practice, but the axes
    /// are reported separately so each can be mapped correctly.
    /// Falls back to (1.0, 1.0) if the visual is not yet connected to
    /// a <see cref="PresentationSource"/>.
    /// </summary>
    public static (double X, double Y) GetScaleXY(Visual visual)
    {
        var compositionTarget = PresentationSource.FromVisual(visual)?.CompositionTarget;
        if (compositionTarget == null)
            return (1.0, 1.0);
        var m = compositionTarget.TransformToDevice;
        return (m.M11, m.M22);
    }

    /// <summary>
    /// Converts physical screen pixels to WPF device-independent
    /// pixels (DIPs) at the given visual's current DPI scale.
    /// </summary>
    public static double PhysicalToLogical(Visual visual, double physicalPixels)
        => physicalPixels / GetScale(visual);
}
