namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Globalization;

/// <summary>What a warm-up capture is: passes the positive control, and is intrusion-free.</summary>
/// <param name="ControlPasses">Whether the capture passes the positive control.</param>
/// <param name="IntrusionFree">Whether the capture's background is clean (no overlay).</param>
internal sealed record ProbeRingWarmUpFrame(bool ControlPasses, bool IntrusionFree);

/// <summary>
/// The warm-up's outcome: whether a usable frame arrived, after how many captures and when, and
/// which conditions were ever seen — so a timeout can name the one that never held.
/// </summary>
/// <param name="Passed">True when a capture was clean, intrusion-free and settled.</param>
/// <param name="Captures">How many captures were taken.</param>
/// <param name="ElapsedMs">The clock reading at the last capture.</param>
/// <param name="LastBytes">The last capture's pixels (the usable frame when passed).</param>
/// <param name="SawControlPass">Whether any capture passed the positive control.</param>
/// <param name="SawIntrusionFree">Whether any capture was intrusion-free.</param>
/// <param name="SawSettled">Whether any capture was byte-identical to its predecessor.</param>
internal sealed record ProbeRingWarmUpResult(
    bool Passed,
    int Captures,
    long ElapsedMs,
    byte[]? LastBytes,
    bool SawControlPass,
    bool SawIntrusionFree,
    bool SawSettled);

/// <summary>
/// R2's warm-up gate (fixup 10 item 1; tightened by fixup 11 R1): the window's first frame was
/// black for about a second in run 3, and the reference captured at that moment anchors every row.
/// The warm-up places the ring at f = 0 and waits, presenting and flushing between captures, until
/// a capture is <b>all three</b> of:
/// <list type="bullet">
/// <item>passing the positive control;</item>
/// <item>intrusion-free (the background rule outside the ring's footprint);</item>
/// <item>settled — byte-identical to the previous warm-up capture (the hardware path is
/// deterministic, so two identical frames mean the window has finished rendering).</item>
/// </list>
/// The 3 s bound is unchanged; on timeout the failure names which condition never held. After a
/// successful warm-up the retake chain starts empty (<c>previousBgra = null</c>): the first plan
/// step is reference B at f = 0, which legitimately equals the last warm-up frame. Warm-up frames
/// are not judged and are not saved.
/// </summary>
internal static class ProbeRingWarmUp
{
    /// <summary>The warm-up bound, in milliseconds. Warm-up plus the captures must stay inside the 30 s Kill.</summary>
    internal const long TimeoutMs = 3000;

    /// <summary>
    /// Waits for the first usable frame: present (via <paramref name="waitOneMoreFrame"/>), capture,
    /// classify, and require control + intrusion-free + settled; repeat until all three hold or the
    /// clock reaches <paramref name="timeoutMilliseconds"/>.
    /// </summary>
    /// <param name="capture">Takes one capture and returns its BGRA bytes.</param>
    /// <param name="classify">Classifies a capture (control, intrusion-free).</param>
    /// <param name="waitOneMoreFrame">Presents the frame and flushes DWM before the next capture.</param>
    /// <param name="elapsedMilliseconds">The clock, in milliseconds.</param>
    /// <param name="timeoutMilliseconds">The bound; <see cref="TimeoutMs"/> by default.</param>
    internal static ProbeRingWarmUpResult Wait(
        Func<byte[]> capture,
        Func<byte[], ProbeRingWarmUpFrame> classify,
        Action waitOneMoreFrame,
        Func<long> elapsedMilliseconds,
        long timeoutMilliseconds = TimeoutMs)
    {
        var captures = 0;
        byte[]? previous = null;
        var sawControl = false;
        var sawClean = false;
        var sawSettled = false;

        while (true)
        {
            waitOneMoreFrame();
            var bytes = capture();
            captures++;

            var frame = classify(bytes);
            var settled = previous is not null && bytes.AsSpan().SequenceEqual(previous);
            sawControl |= frame.ControlPasses;
            sawClean |= frame.IntrusionFree;
            sawSettled |= settled;

            if (frame.ControlPasses && frame.IntrusionFree && settled)
            {
                return new ProbeRingWarmUpResult(
                    true, captures, elapsedMilliseconds(), bytes, sawControl, sawClean, sawSettled);
            }

            if (elapsedMilliseconds() >= timeoutMilliseconds)
            {
                return new ProbeRingWarmUpResult(
                    false, captures, elapsedMilliseconds(), bytes, sawControl, sawClean, sawSettled);
            }

            previous = bytes;
        }
    }

    /// <summary>The session line: <c>R2 warm-up: first ring frame after N captures, T ms</c>.</summary>
    /// <param name="captures">How many captures the warm-up took.</param>
    /// <param name="elapsedMs">The clock at the passing capture.</param>
    internal static string Line(int captures, long elapsedMs) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 warm-up: first ring frame after {captures} captures, {elapsedMs} ms");

    /// <summary>
    /// The failure's line when no usable frame arrived in time, naming the last capture's mean
    /// colour — run 3's black frames read (15, 15, 15) — and which of the three conditions never
    /// held: the positive control, intrusion-free, or settled.
    /// </summary>
    /// <param name="captures">How many captures were taken.</param>
    /// <param name="timeoutMs">The bound that expired.</param>
    /// <param name="mean">The last capture's mean colour.</param>
    /// <param name="sawControlPass">Whether any capture passed the control.</param>
    /// <param name="sawIntrusionFree">Whether any capture was intrusion-free.</param>
    /// <param name="sawSettled">Whether any capture settled.</param>
    internal static string FailureLine(
        int captures, long timeoutMs, (int R, int G, int B) mean,
        bool sawControlPass, bool sawIntrusionFree, bool sawSettled)
    {
        var neverHeld = new System.Collections.Generic.List<string>(3);
        if (!sawControlPass)
            neverHeld.Add("the positive control");
        if (!sawIntrusionFree)
            neverHeld.Add("intrusion-free");
        if (!sawSettled)
            neverHeld.Add("settled (byte-identical to the previous capture)");

        return string.Create(CultureInfo.InvariantCulture,
            $"R2 warm-up failed: no usable ring frame within {timeoutMs} ms after {captures} captures; the last capture's mean colour was ({mean.R}, {mean.G}, {mean.B}). Conditions never held: {string.Join(", ", neverHeld)}. The reference anchors every row, so the run must not continue (run 3's first frame was black for about a second) — check the display and run again.");
    }

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
