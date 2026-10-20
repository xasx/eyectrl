namespace EyeCtrl.Platform.Tests.Input;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using Xunit;

/// <summary>
/// GA1/GA2 (scroll-target fix, Part A): <see cref="Win32InputSimulator.ScrollAt"/> must send ONE
/// atomic batch whose leading absolute move puts the wheel on the requested point. The A3 send
/// seam records the batch; no <c>SendInput</c> ever fires in these tests.
/// </summary>
public class Win32InputSimulatorScrollAtTests
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
    /// GA1, vertical: [MOVE|ABSOLUTE at pt, WHEEL 3×120]. Kills MA1 (leading move dropped) and MA2
    /// (ABSOLUTE dropped from the move).
    /// </summary>
    [Fact]
    public void ScrollAt_SendsAnAbsoluteMove_ThenTheVerticalWheel()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.ScrollAt(new PhysicalPoint(400, 300), deltaX: 0, deltaY: 3);

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(2u);
        var body = sends[0].Inputs;
        Check.That(body).HasSize(2);
        Check.That(body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE).IsNotEqualTo(0u);
        Check.That(body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE).IsNotEqualTo(0u);
        Check.That(body[0].Union.Mouse.Dx).IsEqualTo(400);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo(300);
        Check.That(body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);
    }

    /// <summary>GA1, horizontal: the second leg is an HWHEEL with the negative delta.</summary>
    [Fact]
    public void ScrollAt_SendsAnAbsoluteMove_ThenTheHorizontalWheel()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.ScrollAt(new PhysicalPoint(400, 300), deltaX: -3, deltaY: 0);

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(2u);
        var body = sends[0].Inputs;
        Check.That(body).HasSize(2);
        Check.That(body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE).IsNotEqualTo(0u);
        Check.That(body[0].Union.Mouse.Dx).IsEqualTo(400);
        Check.That(body[0].Union.Mouse.Dy).IsEqualTo(300);
        Check.That(body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_HWHEEL).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(-3 * PlatformConstants.MouseWheelDelta);
    }

    /// <summary>
    /// GA2: ONE send call carries the whole batch, so no other input and no per-frame gaze
    /// MoveCursor can land between the move and the wheel. Kills MA3 (two SendSingleInput calls).
    /// </summary>
    [Fact]
    public void ScrollAt_MakesExactlyOneSendCall_SoTheMoveAndWheelCannotBeInterleaved()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.ScrollAt(new PhysicalPoint(10, 20), deltaX: 0, deltaY: -3);

        Check.WithCustomMessage("the move and the wheel must be one atomic SendInput call.")
            .That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(2u);
        Check.WithCustomMessage("the batch must lead with the absolute move.")
            .That(sends[0].Inputs[0].Union.Mouse.Flags
                & (NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE))
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
    }
}
