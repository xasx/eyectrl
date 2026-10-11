namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// R2's intrusion check (fixup 10 item 2; run 3): every pixel outside the ring's own footprint —
/// the expected ring pattern dilated enough to cover a 1-px placement move plus anti-aliasing —
/// must be the window's background colour. An overlay crossing the capture region (run 3's was the
/// owner's gaze-cursor ring, a localised patch with channel differences up to 75) paints there, and
/// a judged frame carrying it would perturb the measured shift. So an intruded capture is retaken,
/// like a stale one; if the intrusion stands, a judged row fails and the sweep and diagnostics
/// exclude and flag it (<c>intruded=N</c>). The offline analyser applies the same check from the
/// saved frames through <see cref="Mark"/>.
/// </summary>
internal static class ProbeRingIntrusion
{
    /// <summary>
    /// The mask's dilation radius, in pixels: 1 px for a placement move, plus the ring bands'
    /// anti-aliasing on both sides, plus margin.
    /// </summary>
    internal const int MaskDilationPx = 4;

    /// <summary>
    /// A pixel counts as off-background when its largest channel difference from the measured
    /// background colour exceeds this. Measured on the archived runs (probe over all 103 frames of
    /// each): run 2's clean frames read 0 px above every tolerance tried (10–50); run 3's overlay
    /// read 1,759–9,519 px above 20, with a max channel difference of 75. 20 sits between the two
    /// with an order of magnitude of margin on the clean side.
    /// </summary>
    internal const int ChannelTolerance = 20;

    /// <summary>
    /// More than this many mask-outside pixels off-background makes a capture intruded. Measured:
    /// run 2's clean frames never exceed 0 px at any tolerance; run 3's smallest intruded frame
    /// (the solid-look control at f = 0.75 r0) reads 1,759 px above 20. 200 keeps an 8× margin below
    /// the smallest real intrusion and cannot fire on the clean side.
    /// </summary>
    internal const int MinimumPixels = 200;

    /// <summary>Whether pixels outside the dilated ring footprint are the background colour.</summary>
    /// <param name="Intruded">True when more than <see cref="MinimumPixels"/> mask-outside pixels are off-background.</param>
    /// <param name="Pixels">How many mask-outside pixels are off-background (before the threshold).</param>
    /// <param name="MinX">The bounding box of those pixels, x.</param>
    /// <param name="MinY">The bounding box of those pixels, y.</param>
    /// <param name="MaxX">The bounding box of those pixels, right edge.</param>
    /// <param name="MaxY">The bounding box of those pixels, bottom edge.</param>
    internal sealed record Verdict(bool Intruded, int Pixels, int MinX, int MinY, int MaxX, int MaxY)
    {
        /// <summary>The bounding box's width in pixels; 0 when nothing was found.</summary>
        internal int Width => Pixels > 0 ? MaxX - MinX + 1 : 0;

        /// <summary>The bounding box's height in pixels; 0 when nothing was found.</summary>
        internal int Height => Pixels > 0 ? MaxY - MinY + 1 : 0;

        /// <summary>The bbox as the line prints it: <c>x,y,w,h</c>, or <c>-</c> when clean.</summary>
        internal string Bounds => Pixels > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{MinX},{MinY},{Width},{Height}")
            : "-";
    }

    /// <summary>
    /// The protected mask for one look: the frame is rendered offscreen on the capture key (as the
    /// real window is), and a pixel is covered when it differs from the key — the ring's bands and
    /// their anti-aliasing — or lies within <paramref name="dilationPx"/> of a covered pixel
    /// (Chebyshev dilation, separable). Everything else must be background in a clean capture.
    /// Rendering against the key rather than a measured background keeps the mask geometry-only, so
    /// it is correct even for a run whose reference frame is broken (run 3's first frames were
    /// black; its archive's reference background is (15,15,15)).
    /// </summary>
    /// <param name="settings">The look to render.</param>
    /// <param name="centreInCapture">The reference ring centre, in capture-local pixels.</param>
    /// <param name="scale">The run's effective scale.</param>
    /// <param name="dilationPx">The dilation radius; <see cref="MaskDilationPx"/> by default.</param>
    internal static bool[] BackgroundMask(
        CursorSettings settings, PhysicalPoint centreInCapture, double scale, int dilationPx = MaskDilationPx)
    {
        var size = ProbeRingFrameSchedule.CaptureSizePx;
        var bgra = ProbeRingCaptureScene.RenderCapture(
            scale, settings, centreInCapture, snap: false, size);
        var key = ProbeRingCaptureScene.KeyColour;

        var covered = new bool[size * size];
        for (var i = 0; i < covered.Length; i++)
        {
            var p = i * 4;
            covered[i] = bgra[p] != key.B || bgra[p + 1] != key.G || bgra[p + 2] != key.R;
        }

        var horizontal = new bool[covered.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var hit = false;
                var from = Math.Max(0, x - dilationPx);
                var to = Math.Min(size - 1, x + dilationPx);
                for (var xx = from; xx <= to && !hit; xx++)
                    hit = covered[y * size + xx];
                horizontal[y * size + x] = hit;
            }
        }

        var mask = new bool[covered.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var hit = false;
                var from = Math.Max(0, y - dilationPx);
                var to = Math.Min(size - 1, y + dilationPx);
                for (var yy = from; yy <= to && !hit; yy++)
                    hit = horizontal[yy * size + x];
                mask[y * size + x] = hit;
            }
        }

        return mask;
    }

    /// <summary>
    /// The check for one capture: counts the mask-outside pixels whose largest channel difference
    /// from the measured background exceeds <see cref="ChannelTolerance"/>, with their bounding box,
    /// and flags the capture when more than <see cref="MinimumPixels"/> are off-background.
    /// </summary>
    /// <param name="bgra">The captured pixels, BGRA32 row-major.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="backgroundMask">The mask from <see cref="BackgroundMask"/> for this capture's look.</param>
    /// <param name="background">The capture's own measured background colour.</param>
    internal static Verdict Check(
        byte[] bgra, int width, int height, bool[] backgroundMask, (byte R, byte G, byte B) background)
    {
        if (backgroundMask.Length != width * height)
        {
            throw new ArgumentException(
                $"The mask covers {backgroundMask.Length} pixels, not the capture's {width}x{height}.",
                nameof(backgroundMask));
        }

        int pixels = 0, minX = 0, minY = 0, maxX = 0, maxY = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (backgroundMask[index])
                    continue;

                var p = index * 4;
                var dr = Math.Abs(bgra[p + 2] - background.R);
                var dg = Math.Abs(bgra[p + 1] - background.G);
                var db = Math.Abs(bgra[p] - background.B);
                if (Math.Max(dr, Math.Max(dg, db)) <= ChannelTolerance)
                    continue;

                if (pixels == 0)
                {
                    minX = maxX = x;
                    minY = maxY = y;
                }
                else
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }

                pixels++;
            }
        }

        return new Verdict(pixels > MinimumPixels, pixels, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// The session line for one intruded capture:
    /// <c>R2 intrusion: step k (kind f=0.00) N px off-background (bbox x,y,w,h)</c>.
    /// </summary>
    /// <param name="step">The plan step's index.</param>
    /// <param name="kind">The step's kind.</param>
    /// <param name="fraction">The step's scheduled x fraction.</param>
    /// <param name="verdict">The intruded capture's verdict.</param>
    internal static string Line(int step, string kind, double fraction, Verdict verdict) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 intrusion: step {step} ({kind} f={fraction:0.00}) {verdict.Pixels} px off-background (bbox {verdict.Bounds})");

    /// <summary>
    /// The offline marking (fixup 10 item 2, defence in depth): the same check re-derived from the
    /// saved frames — one background mask per look, built at the reference geometry on the look the
    /// sample was drawn with — so live and offline cannot diverge. A manifest's own mark is kept
    /// (OR-ed); samples that were never flagged are returned unchanged.
    /// </summary>
    /// <param name="samples">The loaded samples.</param>
    /// <param name="scale">The run's effective scale.</param>
    internal static IReadOnlyList<ProbeRingCapturedSample> Mark(
        IReadOnlyList<ProbeRingCapturedSample> samples, double scale)
    {
        var referenceB = samples.First(sample =>
            sample.Kind == ProbeRingOnScreenJudge.KindReference
            && sample.Condition == ProbeRingOnScreenJudge.ConditionB
            && sample.Fraction == 0.0);
        var centreInCapture = new PhysicalPoint(
            referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
            referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY);

        var masks = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        var marked = new List<ProbeRingCapturedSample>(samples.Count);
        foreach (var sample in samples)
        {
            if (!masks.TryGetValue(sample.Look.Style, out var mask))
            {
                mask = BackgroundMask(sample.Look.ToSettings(), centreInCapture, scale);
                masks[sample.Look.Style] = mask;
            }

            var verdict = Check(sample.Bgra, sample.SizePx, sample.SizePx, mask, sample.Background);
            marked.Add(verdict.Intruded ? sample with { Intruded = true } : sample);
        }

        return marked;
    }
}
