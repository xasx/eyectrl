namespace EyeCtrl.App.Services;

using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;

/// <summary>
/// A completed screen capture: the image, plus the origin of the capture square it was placed in,
/// in physical screen pixels. The square is centred on the requested point, so <see cref="X"/> and
/// <see cref="Y"/> are negative near an edge — the off-screen part of the image is transparent and
/// <see cref="EffectiveCenter"/> is always the requested point (truncated to whole pixels).
/// </summary>
public readonly record struct CaptureResult(BitmapSource Image, int X, int Y, int Size)
{
    /// <summary>The centre of the captured image, in physical pixels — the requested point.</summary>
    public PhysicalPoint EffectiveCenter => CaptureRegionMath.CenterOf((X, Y, Size));
}
