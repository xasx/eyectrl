namespace OffScreenGazeProbe;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Sample-local stand-in for <c>EyeCtrl.Core.Services.DpiHelper</c>.
/// Copied from <c>samples/StreamEngineGazeTracker/DpiHelper.cs</c> — see that file's
/// docstring for the rationale (probe is intentionally self-contained re: DPI).
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
    /// Converts physical screen pixels to WPF device-independent
    /// pixels (DIPs) at the given visual's current DPI scale.
    /// </summary>
    public static double PhysicalToLogical(Visual visual, double physicalPixels)
        => physicalPixels / GetScale(visual);
}
