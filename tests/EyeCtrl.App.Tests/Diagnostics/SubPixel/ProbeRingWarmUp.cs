namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Globalization;

/// <summary>The warm-up's outcome: whether a ring frame arrived, after how many captures, and when.</summary>
/// <param name="Passed">True when a capture passed the positive control.</param>
/// <param name="Captures">How many captures were taken.</param>
/// <param name="ElapsedMs">The clock reading at the last capture.</param>
/// <param name="LastBytes">The last capture's pixels (the ring frame when passed).</param>
internal sealed record ProbeRingWarmUpResult(bool Passed, int Captures, long ElapsedMs, byte[]? LastBytes);

/// <summary>
/// R2's warm-up gate (fixup 10 item 1; run 3): the window's first frame was black for about a
/// second — run 3's first capture, reference B at f = 0, was uniformly near-black (mean RGB
/// 15,15,15), and the following captures stayed byte-identical to it through their retakes. With a
/// black reference the 21-row scan cannot anchor (it threw "No ring alpha in the scan window"), so
/// the harness now places the ring at f = 0 and waits, presenting and flushing between captures,
/// until a capture passes the positive control — at most <see cref="TimeoutMs"/>.
///
/// <para>
/// After a successful warm-up the retake chain starts empty (<c>previousBgra = null</c>): the first
/// plan step is reference B at f = 0, which legitimately equals the last warm-up frame. Warm-up
/// frames are not judged and are not saved.
/// </para>
/// </summary>
internal static class ProbeRingWarmUp
{
    /// <summary>The warm-up bound, in milliseconds. Warm-up plus the captures must stay inside the 30 s Kill.</summary>
    internal const long TimeoutMs = 3000;

    /// <summary>
    /// Waits for the first ring frame: present (via <paramref name="waitOneMoreFrame"/>), capture,
    /// and check the positive control through <paramref name="isRingFrame"/>; repeat until it
    /// passes or the clock reaches <paramref name="timeoutMilliseconds"/>.
    /// </summary>
    /// <param name="capture">Takes one capture and returns its BGRA bytes.</param>
    /// <param name="isRingFrame">Whether a capture passes the positive control.</param>
    /// <param name="waitOneMoreFrame">Presents the frame and flushes DWM before the next capture.</param>
    /// <param name="elapsedMilliseconds">The clock, in milliseconds.</param>
    /// <param name="timeoutMilliseconds">The bound; <see cref="TimeoutMs"/> by default.</param>
    internal static ProbeRingWarmUpResult Wait(
        Func<byte[]> capture,
        Func<byte[], bool> isRingFrame,
        Action waitOneMoreFrame,
        Func<long> elapsedMilliseconds,
        long timeoutMilliseconds = TimeoutMs)
    {
        var captures = 0;
        while (true)
        {
            waitOneMoreFrame();
            var bytes = capture();
            captures++;
            if (isRingFrame(bytes))
                return new ProbeRingWarmUpResult(true, captures, elapsedMilliseconds(), bytes);
            if (elapsedMilliseconds() >= timeoutMilliseconds)
                return new ProbeRingWarmUpResult(false, captures, elapsedMilliseconds(), bytes);
        }
    }

    /// <summary>The session line: <c>R2 warm-up: first ring frame after N captures, T ms</c>.</summary>
    /// <param name="captures">How many captures the warm-up took.</param>
    /// <param name="elapsedMs">The clock at the passing capture.</param>
    internal static string Line(int captures, long elapsedMs) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 warm-up: first ring frame after {captures} captures, {elapsedMs} ms");

    /// <summary>
    /// The failure's line when no ring frame arrived in time, naming the last capture's mean colour
    /// — run 3's black frames read (15, 15, 15) — so the session record says what the screen held.
    /// </summary>
    /// <param name="captures">How many captures were taken.</param>
    /// <param name="timeoutMs">The bound that expired.</param>
    /// <param name="mean">The last capture's mean colour.</param>
    internal static string FailureLine(int captures, long timeoutMs, (int R, int G, int B) mean) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 warm-up failed: no ring frame within {timeoutMs} ms after {captures} captures; the last capture's mean colour was ({mean.R}, {mean.G}, {mean.B}). The window had not rendered its ring (run 3's first frame was black for about a second) — check the display and run again.");

    /// <summary>The mean BGRA colour of a capture, for the failure line.</summary>
    /// <param name="bgra">The captured pixels, BGRA32 row-major.</param>
    internal static (int R, int G, int B) MeanColour(byte[] bgra)
    {
        if (bgra.Length < 4)
            throw new ArgumentException("A frame needs at least one pixel.", nameof(bgra));

        long r = 0, g = 0, b = 0;
        var pixels = bgra.Length / 4;
        for (var i = 0; i < pixels; i++)
        {
            var p = i * 4;
            b += bgra[p];
            g += bgra[p + 1];
            r += bgra[p + 2];
        }

        return ((int)(r / pixels), (int)(g / pixels), (int)(b / pixels));
    }
}
