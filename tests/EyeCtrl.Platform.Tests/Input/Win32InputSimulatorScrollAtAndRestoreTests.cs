namespace EyeCtrl.Platform.Tests.Input;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using Xunit;

/// <summary>
/// GB1/GB2 (scroll-at-P, Part B): <see cref="Win32InputSimulator.ScrollAtAndRestore"/> must send
/// ONE atomic batch of [MOVE|ABSOLUTE at, wheel(s), MOVE|ABSOLUTE restore]. The A3 seam records the
/// batch; no <c>SendInput</c> ever fires in these tests.
/// </summary>
public class Win32InputSimulatorScrollAtAndRestoreTests
{
    private static (Win32InputSimulator Sim, List<(uint Count, NativeMethods.INPUT[] Inputs)> Sends)
        NewRecordingSimulator()
    {
        var sim = new Win32InputSimulator(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());
        var sends = new List<(uint Count, NativeMethods.INPUT[] Inputs)>();
        sim.SendInputOverride = (count, inputs, _) =>
        {
            sends.Add((count, inputs.ToArray()));
            return (int)count;
        };
        return (sim, sends);
    }

    /// <summary>
    /// GB1: exactly [MOVE|ABS anchor, WHEEL 3×120, MOVE|ABS cursor]. Kills MB1 (no restore move),
    /// MB2 (restore to the anchor) and MB3 (the wheel before the first move).
    /// </summary>
    [Fact]
    public void GB1_BatchIsMoveToAnchor_ThenWheel_ThenMoveToTheFreshCursor()
    {
        // The anchor leg goes through the pass-through mapper; the restore leg uses the live
        // screen bounds + the exact encoding, so derive both points from the live metrics.
        var (width, height) = Win32InputSimulator.ScreenMetrics;
        var at = new PhysicalPoint(width / 4, height / 4);
        var restore = new PhysicalPoint(width * 3 / 4, height * 3 / 4);
        var (sim, sends) = NewRecordingSimulator();

        sim.ScrollAtAndRestore(at, restore, deltaX: 0, deltaY: 3);

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(3u);
        var body = sends[0].Inputs;
        Check.That(body).HasSize(3);

        Check.WithCustomMessage("element 0 must be the absolute move to the anchor.")
            .That(body[0].Union.Mouse.Flags
                & (NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE))
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        Check.That(body[0].Union.Mouse.Dx).IsEqualTo((int)at.X);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo((int)at.Y);

        Check.That(body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);

        Check.WithCustomMessage("the last element must be the absolute move back to the live cursor.")
            .That(body[2].Union.Mouse.Flags
                & (NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE))
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        Check.WithCustomMessage($"the restore leg must decode to ({restore.X:F0},{restore.Y:F0}).")
            .That((int)(((long)body[2].Union.Mouse.Dx * width) >> 16)).IsEqualTo((int)restore.X);
        Check.That((int)(((long)body[2].Union.Mouse.Dy * height) >> 16)).IsEqualTo((int)restore.Y);
    }

    /// <summary>
    /// GB2: the move, wheel and restore ride in ONE send call, so the cursor cannot be left at the
    /// anchor by an interruption between the legs. Kills MB4 (separate sends).
    /// </summary>
    [Fact]
    public void GB2_IsOneAtomicSendCall()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.ScrollAtAndRestore(
            new PhysicalPoint(10, 20), new PhysicalPoint(30, 40), deltaX: -1, deltaY: 0);

        Check.WithCustomMessage("the hop must be one atomic SendInput call.").That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(3u);
        Check.That(sends[0].Inputs[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE).IsNotEqualTo(0u);
        Check.That(sends[0].Inputs[2].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE).IsNotEqualTo(0u);
        Check.That(sends[0].Inputs[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_HWHEEL).IsNotEqualTo(0u);
    }

    /// <summary>
    /// GF1: the real restore input builder round-trips EVERY pixel under Windows' truncating
    /// decode. 1920×1080 is where the 65535 cap matters: ceil(1919 × 65536 / 1920) is 65536
    /// without it, which decodes to 1920 — one pixel past the screen. Kills MF1b (floor) and
    /// MF1c (cap dropped).
    /// </summary>
    [Theory]
    [InlineData(3840, 2400)]
    [InlineData(1920, 1080)]
    public void GF1_EveryPixel_RoundTripsUnderWindowsTruncatingDecode(int width, int height)
    {
        var misses = new List<string>();
        for (int x = 0; x < width; x++)
        {
            var input = Win32InputSimulator.BuildRestoreMouseInput(new PhysicalPoint(x, 0), width, height);
            var decoded = (int)(((long)input.Union.Mouse.Dx * width) >> 16);
            if (decoded != x) misses.Add($"x {x} -> {input.Union.Mouse.Dx} -> {decoded}");
        }
        for (int y = 0; y < height; y++)
        {
            var input = Win32InputSimulator.BuildRestoreMouseInput(new PhysicalPoint(0, y), width, height);
            var decoded = (int)(((long)input.Union.Mouse.Dy * height) >> 16);
            if (decoded != y) misses.Add($"y {y} -> {input.Union.Mouse.Dy} -> {decoded}");
        }

        Check.WithCustomMessage(
                $"restore round-trip misses at {width}×{height}: {string.Join(" | ", misses.Take(8))}")
            .That(misses).IsEmpty();

        // The shared mapper clamps to [0, screenSize] INCLUSIVE, so the clamped right/bottom edge
        // is an input too. The 65535 cap must keep it on the last pixel; without the cap the
        // encoding is 65536 and decodes to `screenSize`, one pixel off the screen (MF1c).
        var edge = Win32InputSimulator.BuildRestoreMouseInput(
            new PhysicalPoint(width, height), width, height);
        Check.WithCustomMessage($"the clamped edge encodes to {edge.Union.Mouse.Dx}, not 65535.")
            .That(edge.Union.Mouse.Dx).IsEqualTo(65535);
        Check.That(edge.Union.Mouse.Dy).IsEqualTo(65535);
        Check.That((int)(((long)edge.Union.Mouse.Dx * width) >> 16)).IsEqualTo(width - 1);
        Check.That((int)(((long)edge.Union.Mouse.Dy * height) >> 16)).IsEqualTo(height - 1);
    }

    /// <summary>
    /// GF1: the BATCH's actual restore leg (the wiring, not just the helper) on the live screen.
    /// The last pixel must decode to itself; the shared rounding mapper lands it one pixel low
    /// (3839 → 3838 at 3840), so MF1a is RED here.
    /// </summary>
    [Fact]
    public void GF1_TheBatchedRestoreLeg_RestoresTheLastPixelExactly()
    {
        var (width, height) = Win32InputSimulator.ScreenMetrics;
        var sim = new Win32InputSimulator(new MouseCoordinateMapper());
        var last = new PhysicalPoint(width - 1, height - 1);

        var body = sim.BuildWheelAtAndRestoreBody(
            new PhysicalPoint(1, 1), last, deltaX: 0, deltaY: 3);
        var restore = body[^1].Union.Mouse;

        Check.That(restore.Flags & NativeMethods.MOUSEEVENTF_MOVE).IsNotEqualTo(0u);
        Check.That(restore.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE).IsNotEqualTo(0u);
        Check.WithCustomMessage(
                $"restore Dx {restore.Dx} on {width} decodes to {((long)restore.Dx * width) >> 16}, not {width - 1}.")
            .That((int)(((long)restore.Dx * width) >> 16)).IsEqualTo(width - 1);
        Check.WithCustomMessage(
                $"restore Dy {restore.Dy} on {height} decodes to {((long)restore.Dy * height) >> 16}, not {height - 1}.")
            .That((int)(((long)restore.Dy * height) >> 16)).IsEqualTo(height - 1);
    }

    /// <summary>
    /// GF1b: the leading move to the anchor still uses the shared rounding normaliser — the same
    /// Dx/Dy as <see cref="Win32InputSimulator.BuildModifiedWheelBody"/> — and NOT the exact
    /// restore encoding. Kills the "apply the new encoding to the anchor leg too" mutant.
    /// </summary>
    [Fact]
    public void GF1b_TheAnchorLeg_StillUsesTheSharedRoundingNormaliser()
    {
        var (width, height) = Win32InputSimulator.ScreenMetrics;
        var sim = new Win32InputSimulator(new MouseCoordinateMapper());
        var anchor = new PhysicalPoint(width - 1, height - 1);

        var body = sim.BuildWheelAtAndRestoreBody(
            anchor, new PhysicalPoint(1, 1), deltaX: 0, deltaY: 3);
        var modifiedBody = sim.BuildModifiedWheelBody(anchor, deltaX: 0, deltaY: 3);
        var expected = new MouseCoordinateMapper().NormalizeScreenCoordinate(anchor, width, height);

        Check.WithCustomMessage("the anchor leg must be byte-identical to the modified wheel's move.")
            .That(body[0].Union.Mouse.Dx).IsEqualTo(modifiedBody[0].Union.Mouse.Dx);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo(modifiedBody[0].Union.Mouse.Dy);
        Check.That(body[0].Union.Mouse.Dx).IsEqualTo(expected.normalizedX);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo(expected.normalizedY);

        Check.WithCustomMessage("the exact restore encoding must not be applied to the anchor leg.")
            .That(body[0].Union.Mouse.Dx)
            .IsNotEqualTo(Win32InputSimulator.EncodeRestoreCoordinate(anchor.X, width));
    }
}
