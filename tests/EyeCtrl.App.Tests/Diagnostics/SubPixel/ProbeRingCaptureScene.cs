namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Builds the frames R2 measures: a magenta capture key with the probe's real ring
/// (<see cref="ProbeRingVisual"/>, C4) placed at a scheduled physical centre. The headless analysis
/// tests feed these straight into <see cref="ProbeRingCaptureAnalysis"/>; the on-screen harness uses
/// the same renderer for its expected-pattern frame.
///
/// <para>
/// <b>Why magenta.</b> The capture key must not collide with any ring band: every theme's halo is
/// near-black (<c>#0A0A14</c> in Midnight) and the core is the owner's colour, so a saturated
/// magenta (<c>#FF00FF</c>) is far from both. The analysis still measures the background from the
/// capture rather than trusting this constant — the constant only guarantees the key starts distant.
/// </para>
///
/// <para>
/// <b>Why the look opacity lives here.</b> The ring is drawn in the quiet look, the same container
/// opacity R1's fractional assertions measure at; the transparency composite is what R2's hardware
/// path must reproduce.
/// </para>
/// </summary>
internal static class ProbeRingCaptureScene
{
    /// <summary>The owner's ring diameter, in DIPs.</summary>
    internal const double RingSizeDip = 250.0;

    /// <summary>The owner's stroke thickness, in DIPs.</summary>
    internal const double RingThickness = 1.0;

    /// <summary>The capture key the synthetic frames are drawn on.</summary>
    internal static readonly Color KeyColour = Color.FromRgb(0xFF, 0x00, 0xFF);

    /// <summary>The look the ring is measured in; R1's fractional cases use the same quiet look.</summary>
    internal const double LookOpacity = ProbeRingVisual.QuietLookOpacity;

    /// <summary>The owner's look: BiTonal at 250 DIP and thickness 1 (the ticket's (d)).</summary>
    internal static CursorSettings OwnerLook() => new()
    {
        RingStyle = CursorRingStyle.BiTonalSegments,
        CircleSize = RingSizeDip,
        CircleThickness = RingThickness,
        CircleOpacity = 1.0,
        CircleColor = "#A5F3FC",
    };

    /// <summary>
    /// Renders one frame: a <paramref name="captureSizePx"/>-square capture-key surface with the
    /// ring's physical top-left placed from <paramref name="centreCapturePx"/> (capture-local
    /// physical pixels) by the same <see cref="ProbeRingVisual"/> placement production uses.
    /// </summary>
    /// <param name="scale">Device pixels per DIP.</param>
    /// <param name="settings">The ring look to draw.</param>
    /// <param name="centreCapturePx">The ring centre, in capture-local physical pixels.</param>
    /// <param name="snap">Whether the top-left snaps to whole device pixels (condition A).</param>
    /// <param name="captureSizePx">The square frame's size in pixels.</param>
    /// <returns>The frame's BGRA32 pixels, row-major.</returns>
    internal static byte[] RenderCapture(
        double scale,
        CursorSettings settings,
        PhysicalPoint centreCapturePx,
        bool snap,
        int captureSizePx)
    {
        var sceneDip = captureSizePx / scale;
        var scene = new Canvas
        {
            Width = sceneDip,
            Height = sceneDip,
            Background = new SolidColorBrush(KeyColour),
        };

        var ring = new ProbeRingVisual(scale, settings, LookOpacity);
        ring.Place(centreCapturePx, snap);
        scene.Children.Add(ring);
        scene.Measure(new Size(sceneDip, sceneDip));
        scene.Arrange(new Rect(0, 0, sceneDip, sceneDip));
        scene.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            captureSizePx, captureSizePx, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
        bitmap.Render(scene);

        var stride = captureSizePx * 4;
        var pixels = new byte[stride * captureSizePx];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// Doubles a frame by pixel replication: the synthetic stand-in for a 96-DPI window that DWM
    /// bitmap-stretches to a 192-DPI display. Every source pixel becomes a 2x2 block, so a scheduled
    /// fraction in DIPs lands on twice as many device pixels — the pathology the DPI check exists
    /// for, reproduced on the analysis path.
    /// </summary>
    /// <param name="bgra">The source frame, BGRA32, row-major.</param>
    /// <param name="width">The source width in pixels.</param>
    /// <param name="height">The source height in pixels.</param>
    internal static byte[] Stretch2x(byte[] bgra, int width, int height)
    {
        var wide = width * 2;
        var tall = height * 2;
        var result = new byte[wide * tall * 4];
        for (var y = 0; y < tall; y++)
        {
            for (var x = 0; x < wide; x++)
            {
                var source = ((y / 2) * width + (x / 2)) * 4;
                var target = (y * wide + x) * 4;
                result[target] = bgra[source];
                result[target + 1] = bgra[source + 1];
                result[target + 2] = bgra[source + 2];
                result[target + 3] = bgra[source + 3];
            }
        }

        return result;
    }
}
