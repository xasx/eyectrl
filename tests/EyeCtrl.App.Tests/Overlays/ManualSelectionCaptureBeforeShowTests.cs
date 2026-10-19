namespace EyeCtrl.App.Tests.Overlays;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// G-A1 (the magnifier fix's main defect, M1): the big view used to be shown BEFORE its picture
/// existed. <c>Show</c> put the window up, then an <c>async void</c> continuation waited
/// <c>Task.Delay(100)</c> and captured on the UI thread. Every session therefore first showed the
/// PREVIOUS session's screenshot, and the first session of a process showed an empty, see-through
/// circle — literally "not zoomed".
///
/// <para>
/// The fix captures synchronously inside <c>UpdateCenter</c>, which <c>Show</c> already calls before
/// <c>base.Show()</c>, so the first visible frame is this session's picture. The gate is deliberately
/// a "no dispatcher pump" assertion: with the delayed path restored, the property is still the old
/// image at this point and the test goes red — which is exactly what a pump would mask.
/// </para>
///
/// <para>
/// Construction-only, per the ticket's hard constraint: the real <see cref="ManualSelectionOverlayWindow"/>
/// is never shown. The capture is an injected seam, so no screen pixel is read.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionCaptureBeforeShowTests
{
    [StaFact]
    public void Recapture_ReplacesTheOldPictureSynchronously_NoDispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            // The previous session's picture, exactly the state the old code presented for ≥100 ms.
            window.ManualSelectionBrush.ImageSource = Solid(4, 20);

            var fresh = Solid(4, 200);
            window.Capture = (_, _) => new CaptureResult(fresh, 0, 0, 4);

            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0);

            Check.WithCustomMessage(
                    "the picture is not the seam's new image straight after UpdateCenter; the show path "
                    + "must capture synchronously, before base.Show() presents the first frame.")
                .That(window.ManualSelectionBrush.ImageSource).IsSameReferenceAs(fresh);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void FirstSession_StartsEmpty_AndHasAPictureAfterUpdateCenter()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            Check.That(window.ManualSelectionBrush.ImageSource).IsNull();

            var fresh = Solid(4, 128);
            window.Capture = (_, _) => new CaptureResult(fresh, 0, 0, 4);

            window.UpdateCenter(new PhysicalPoint(1000, 1000), 170, 2.0, new PhysicalPoint(990, 1010));

            Check.That(window.ManualSelectionBrush.ImageSource).IsSameReferenceAs(fresh);
            // The effective centre follows the picture, in the same synchronous step.
            Check.That(window.EffectiveCaptureCenter).IsEqualTo(new PhysicalPoint(2, 2));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An opaque grey bitmap of the given side; its FormatConvertedBitmap-friendly source.</summary>
    internal static BitmapSource Solid(int side, byte value) =>
        BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null,
            Enumerable.Repeat(value, side * side).ToArray(), side);
}
