namespace EyeCtrl.Platform.Tests.Input;

using EyeCtrl.Platform.Input;
using Xunit;

/// <summary>
/// C3's recovery release: <see cref="Win32InputSimulator.ReleaseLeftButton"/> must send exactly
/// one <c>LEFTUP</c> and NO move flags — <see cref="Win32InputSimulator.MouseUp"/> carries an
/// absolute move, which would teleport the cursor to an invented point (a killed drag leaves no
/// trustworthy one). The send seam records the batch; no <c>SendInput</c> ever fires.
/// </summary>
public class Win32InputSimulatorReleaseLeftButtonTests
{
    [Fact]
    public void ReleaseLeftButton_SendsOneLeftUp_WithNoMove()
    {
        var sim = new Win32InputSimulator(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());
        var sends = new List<(uint Count, NativeMethods.INPUT[] Inputs)>();
        sim.SendInputOverride = (count, inputs, _) =>
        {
            sends.Add((count, inputs.ToArray()));
            return (int)count;
        };

        sim.ReleaseLeftButton();

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(1u);
        Check.That(sends[0].Inputs).HasSize(1);
        var mouse = sends[0].Inputs[0].Union.Mouse;
        Check.That(mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP).IsNotEqualTo(0u);
        Check.That(mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE).IsEqualTo(0u);
        Check.That(mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE).IsEqualTo(0u);
        Check.That(sends[0].Inputs[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
    }
}
