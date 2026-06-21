namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class ControlInfoTests
{
    [Fact]
    public void ControlInfo_CenterPoint_ReturnsRectCenter()
    {
        var rect = new PhysicalRect(10, 20, 100, 50);
        var info = new ControlInfo(rect);
        Check.That(info.CenterPoint.X).IsEqualTo(60);
        Check.That(info.CenterPoint.Y).IsEqualTo(45);
    }

    [Fact]
    public void PhysicalRect_Center_ReturnsCorrectValues()
    {
        var rect = new PhysicalRect(0, 0, 200, 100);
        Check.That(rect.Center.X).IsEqualTo(100);
        Check.That(rect.Center.Y).IsEqualTo(50);
    }

    [Fact]
    public void SelectionMode_Defaults()
    {
        // default(int) = 0 = None
        Check.That(default(SelectionMode)).IsEqualTo(SelectionMode.None);
    }
}
