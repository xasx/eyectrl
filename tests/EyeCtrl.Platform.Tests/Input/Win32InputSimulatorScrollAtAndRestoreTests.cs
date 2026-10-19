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
        var (sim, sends) = NewRecordingSimulator();
        var at = new PhysicalPoint(1000, 1000);
        var restore = new PhysicalPoint(1800, 1200);

        sim.ScrollAtAndRestore(at, restore, deltaX: 0, deltaY: 3);

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(3u);
        var body = sends[0].Inputs;
        Check.That(body).HasSize(3);

        Check.WithCustomMessage("element 0 must be the absolute move to the anchor.")
            .That(body[0].Union.Mouse.Flags
                & (NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE))
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        Check.That(body[0].Union.Mouse.Dx).IsEqualTo(1000);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo(1000);

        Check.That(body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);

        Check.WithCustomMessage("the last element must be the absolute move back to the live cursor.")
            .That(body[2].Union.Mouse.Flags
                & (NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE))
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        Check.That(body[2].Union.Mouse.Dx).IsEqualTo(1800);
        Check.That(body[2].Union.Mouse.Dy).IsEqualTo(1200);
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
}
