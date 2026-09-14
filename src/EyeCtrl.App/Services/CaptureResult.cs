namespace EyeCtrl.App.Services;

using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;

/// <summary>
/// A completed screen capture: the image, plus the rectangle of the screen it was taken from in
/// physical pixels. The rectangle is the effective one — the capture request is clamped to the
/// screen — so <see cref="EffectiveCenter"/> is where the image is actually centred, which near an
/// edge is not the centre that was requested.
/// </summary>
public readonly record struct CaptureResult(BitmapSource Image, int X, int Y, int Size)
{
    /// <summary>The centre of the captured image, in physical pixels.</summary>
    public PhysicalPoint EffectiveCenter => CaptureRegionMath.CenterOf((X, Y, Size));
}
