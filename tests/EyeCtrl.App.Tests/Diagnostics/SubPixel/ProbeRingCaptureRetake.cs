namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Globalization;

/// <summary>The result of <see cref="ProbeRingCaptureRetake.CaptureWithRetake"/>: the delivered bytes and what it took.</summary>
/// <param name="Bytes">The accepted capture's bytes (the last one taken).</param>
/// <param name="Retakes">How many extra captures were taken because the previous returned a stale copy.</param>
/// <param name="Stale">Whether the accepted bytes are STILL a stale copy after the retakes.</param>
internal sealed record ProbeRingRetakeResult(byte[] Bytes, int Retakes, bool Stale);

/// <summary>
/// R2's stale-capture retake (fixup 08 item 2), pure and headless-testable: consecutive plan steps
/// never share a placement (pinned), so a capture whose BGRA bytes equal the <b>previous</b>
/// capture's bytes is, by definition, the previous frame — the screen did not repaint. The caller
/// waits one more frame (present + DwmFlush) and the capture is taken again, up to
/// <see cref="MaxRetakes"/> times.
///
/// <para>
/// A capture that is still stale after the retakes is marked, never dropped: a judged row fails on
/// it ("stale frame") and the sweep/diagnostics exclude and flag it. The live harness prints a
/// <c>R2 retake: …</c> line per retake and a <c>R2 stale: …</c> line when the mark stands; the
/// offline analyser applies the same rule to a saved archive.
/// </para>
/// </summary>
internal static class ProbeRingCaptureRetake
{
    /// <summary>How many extra captures a stale frame may trigger before it is marked.</summary>
    internal const int MaxRetakes = 3;

    /// <summary>
    /// Captures once, and while the bytes are byte-identical to <paramref name="previous"/> — a
    /// stale copy — waits one more frame and captures again, up to <paramref name="maxRetakes"/>
    /// times. <paramref name="previous"/> null means there is nothing to be stale relative to.
    /// </summary>
    /// <param name="capture">Takes one capture and returns its BGRA bytes.</param>
    /// <param name="previous">The previous capture's BGRA bytes, or null for the first capture.</param>
    /// <param name="waitOneMoreFrame">Presents the frame and flushes DWM once more before a retake.</param>
    /// <param name="maxRetakes">The retake bound; 3 by default.</param>
    internal static ProbeRingRetakeResult CaptureWithRetake(
        Func<byte[]> capture, byte[]? previous, Action waitOneMoreFrame, int maxRetakes = MaxRetakes)
    {
        var bytes = capture();
        var retakes = 0;
        while (previous is not null && retakes < maxRetakes && bytes.AsSpan().SequenceEqual(previous))
        {
            waitOneMoreFrame();
            bytes = capture();
            retakes++;
        }

        return new ProbeRingRetakeResult(
            bytes, retakes, previous is not null && bytes.AsSpan().SequenceEqual(previous));
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
