namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class GazePointTests
{
    [Fact]
    public void GazePoint_IsValueType_ForAllocationFreeStreaming()
    {
        Check.That(typeof(GazePoint).IsValueType).IsTrue();
    }

    [Fact]
    public void GazePoint_ValueEquality_Holds()
    {
        var a = new GazePoint(1.5, 2.5, 1000, true);
        var b = new GazePoint(1.5, 2.5, 1000, true);
        Check.That(a).IsEqualTo(b);
    }
}
