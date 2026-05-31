namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class ControlInfoTests
{
    [Fact]
    public void ControlInfo_CenterPoint_ReturnsRectCenter()
    {
        var rect = new PhysicalRect(10, 20, 100, 50);
        var info = new ControlInfo(rect);
        Assert.Equal(60, info.CenterPoint.X); // 10 + 100/2
        Assert.Equal(45, info.CenterPoint.Y); // 20 + 50/2
    }

    [Fact]
    public void PhysicalRect_Center_ReturnsCorrectValues()
    {
        var rect = new PhysicalRect(0, 0, 200, 100);
        Assert.Equal(100, rect.Center.X);
        Assert.Equal(50, rect.Center.Y);
    }

    [Fact]
    public void SelectionMode_Defaults()
    {
        // default(int) = 0 = None
        Assert.Equal(SelectionMode.None, default(SelectionMode));
    }
}
