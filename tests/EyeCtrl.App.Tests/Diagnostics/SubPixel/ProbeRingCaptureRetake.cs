namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>The result of <see cref="ProbeRingCaptureRetake.CaptureWithRetake"/>: the delivered bytes and what it took.</summary>
/// <param name="Bytes">The accepted capture's bytes (the last one taken).</param>
/// <param name="Retakes">How many extra captures were taken because the previous was stale or intruded.</param>
/// <param name="Stale">Whether the accepted bytes are STILL a stale copy after the retakes.</param>
/// <param name="Intruded">Whether the accepted bytes still break the background rule after the retakes.</param>
/// <param name="Intrusions">Every intruded capture observed, in order — the ones that triggered retakes plus the final one when the mark stands (fixup 10 item 2).</param>
internal sealed record ProbeRingRetakeResult(
    byte[] Bytes,
    int Retakes,
    bool Stale,
    bool Intruded = false,
    IReadOnlyList<ProbeRingIntrusion.Verdict>? Intrusions = null);

/// <summary>
/// R2's stale-capture retake (fixup 08 item 2; extended by fixup 10 item 2 with the intrusion
/// check), pure and headless-testable: consecutive plan steps never share a placement (pinned), so
/// a capture whose BGRA bytes equal the <b>previous</b> capture's bytes is, by definition, the
/// previous frame — the screen did not repaint. And a capture whose pixels break the background
/// rule outside the ring's dilated footprint (<paramref name="classifyIntrusion"/>) carries an
/// overlay. Either condition triggers a retake: the caller waits one more frame (present +
/// DwmFlush) and the capture is taken again, up to <see cref="MaxRetakes"/> times.
///
/// <para>
/// A capture that is still stale or intruded after the retakes is marked, never dropped: a judged
/// row fails on it ("stale frame" / "an overlay covered the capture region") and the
/// sweep/diagnostics exclude and flag it. The live harness prints a <c>R2 retake: …</c> line per
/// retake, a <c>R2 stale: …</c> line when the stale mark stands, and a <c>R2 intrusion: …</c> line
/// per intruded capture; the offline analyser applies the same rules to a saved archive.
/// </para>
/// </summary>
internal static class ProbeRingCaptureRetake
{
    /// <summary>How many extra captures a stale or intruded frame may trigger before it is marked.</summary>
    internal const int MaxRetakes = 3;

    /// <summary>
    /// Captures once, and while the bytes are byte-identical to <paramref name="previous"/> — a
    /// stale copy — or the intrusion classifier flags them, waits one more frame and captures
    /// again, up to <paramref name="maxRetakes"/> times. <paramref name="previous"/> null means
    /// there is nothing to be stale relative to; a null classifier skips the intrusion check.
    /// </summary>
    /// <param name="capture">Takes one capture and returns its BGRA bytes.</param>
    /// <param name="previous">The previous capture's BGRA bytes, or null for the first capture.</param>
    /// <param name="waitOneMoreFrame">Presents the frame and flushes DWM once more before a retake.</param>
    /// <param name="maxRetakes">The retake bound; 3 by default.</param>
    /// <param name="classifyIntrusion">Classifies a capture's pixels for the overlay check; null skips it.</param>
    internal static ProbeRingRetakeResult CaptureWithRetake(
        Func<byte[]> capture,
        byte[]? previous,
        Action waitOneMoreFrame,
        int maxRetakes = MaxRetakes,
        Func<byte[], ProbeRingIntrusion.Verdict>? classifyIntrusion = null)
    {
        var intrusions = new List<ProbeRingIntrusion.Verdict>();
        var bytes = capture();
        var retakes = 0;
        ProbeRingIntrusion.Verdict? last = null;
        while (true)
        {
            last = classifyIntrusion?.Invoke(bytes);
            if (last?.Intruded == true)
                intrusions.Add(last);

            var stale = previous is not null && bytes.AsSpan().SequenceEqual(previous);
            if (retakes >= maxRetakes || (!stale && last?.Intruded != true))
                break;

            waitOneMoreFrame();
            bytes = capture();
            retakes++;
        }

        return new ProbeRingRetakeResult(
            bytes,
            retakes,
            previous is not null && bytes.AsSpan().SequenceEqual(previous),
            last?.Intruded == true,
            intrusions);
    }

    /// <summary>
    /// The session line for one retake: <c>R2 retake: step k (kind f=0.00) after N</c>, where
    /// <c>N</c> is which retake this was (1..3).
    /// </summary>
    /// <param name="step">The plan step's index.</param>
    /// <param name="kind">The step's kind (reference / assertion / sweep / solid-control / x-only).</param>
    /// <param name="fraction">The step's scheduled x fraction.</param>
    /// <param name="retake">Which retake this line records, 1-based.</param>
    internal static string RetakeLine(int step, string kind, double fraction, int retake) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 retake: step {step} ({kind} f={fraction:0.00}) after {retake}");

    /// <summary>
    /// The session line for a capture still stale after the retakes: <c>R2 stale: step k (kind
    /// f=0.00) after N retakes</c> — a judged row fails on it; the sweep and diagnostics exclude
    /// and flag it.
    /// </summary>
    /// <param name="step">The plan step's index.</param>
    /// <param name="kind">The step's kind.</param>
    /// <param name="fraction">The step's scheduled x fraction.</param>
    /// <param name="retakes">How many retakes were taken before the mark stood.</param>
    internal static string StaleLine(int step, string kind, double fraction, int retakes) =>
        string.Create(CultureInfo.InvariantCulture,
            $"R2 stale: step {step} ({kind} f={fraction:0.00}) after {retakes} retakes — still a byte-identical copy of the previous frame");
}
