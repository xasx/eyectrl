namespace EyeCtrl.App.Tests.Overlays;

using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// T6 (magnifier-capture-place-investigation fix, C5): the menu preview now uses the centred
/// capture, so at an edge its off-screen part is transparent. Without an under-fill the menu's
/// backdrop would show through the preview circle. The preview must therefore insert an opaque
/// <see cref="Tokens.BrushSurfaceDark"/> ellipse below the picture, and both below the buttons.
///
/// <para>
/// Constructed window, injected capture seam, no <c>Show()</c>: nothing is painted on the owner's
/// screen and no screen pixel is read.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ActionMenuPreviewUnderFillTests
{
    [StaFact]
    public void ThePreview_InsertsAnOpaqueUnderFill_BelowThePicture_AndBelowTheButtons()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActionMenuWindow();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            // A stand-in for the ring's buttons; the preview must stay behind it.
            var button = new Border { Width = 10, Height = 10 };
            window.MenuCanvas.Children.Add(button);

            window.PreviewCapture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(200, 128), 0, 0, 200);

            var task = window.CapturePreviewAsync(
                new Point(200, 200), 200, new PhysicalPoint(500, 500), 100, 1.0, "#FFFFFF",
                CancellationToken.None);
            PumpUntil(() => task.IsCompleted);

            var children = window.MenuCanvas.Children;
            var ellipses = children.OfType<Ellipse>().ToList();
            var imageEllipse = ellipses.Single(e => e.Fill is ImageBrush);
            var underFill = ellipses.SingleOrDefault(e => ReferenceEquals(e.Fill, Tokens.BrushSurfaceDark));

            Check.WithCustomMessage(
                    "no opaque BrushSurfaceDark under-fill was inserted; the transparent off-screen part "
                    + "would show the menu backdrop through the preview.")
                .That(underFill).IsNotNull();

            var underIndex = children.IndexOf(underFill!);
            var imageIndex = children.IndexOf(imageEllipse);
            Check.WithCustomMessage("the under-fill must paint BELOW the captured picture.")
                .That(underIndex).IsStrictlyLessThan(imageIndex);
            Check.WithCustomMessage("both preview ellipses must stay behind the buttons.")
                .That(imageIndex).IsStrictlyLessThan(children.IndexOf(button));

            Check.That(underFill!.Width).IsEqualTo(200);
            Check.That(underFill.Height).IsEqualTo(200);
            Check.That(Canvas.GetLeft(underFill)).IsEqualTo(100);
            Check.That(Canvas.GetTop(underFill)).IsEqualTo(100);
            Check.WithCustomMessage("the under-fill must not draw a rim (the picture's ellipse owns it).")
                .That(underFill.Stroke).IsNull();

            Check.That(Canvas.GetLeft(imageEllipse)).IsEqualTo(100);
            Check.That(Canvas.GetTop(imageEllipse)).IsEqualTo(100);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            window.Close();
        }
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var started = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - started < timeoutMs)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        if (!condition()) throw new TimeoutException("the pumped dispatcher never completed the preview capture");
    }
}
