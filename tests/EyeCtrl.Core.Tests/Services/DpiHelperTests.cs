namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Services;
using Xunit;

public class DpiHelperTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PhysicalToLogical_DividesByScale(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        Assert.Equal(100.0, DpiHelper.PhysicalToLogicalX(100.0 * scale), 5);
        Assert.Equal(100.0, DpiHelper.PhysicalToLogicalY(100.0 * scale), 5);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void LogicalToPhysical_MultipliesByScale(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        Assert.Equal(100.0 * scale, DpiHelper.LogicalToPhysicalX(100.0), 5);
        Assert.Equal(100.0 * scale, DpiHelper.LogicalToPhysicalY(100.0), 5);
    }

    [Fact]
    public void DefaultScale_IsOne()
    {
        DpiHelper.Initialize(1.0, 1.0);
        Assert.Equal(500.0, DpiHelper.PhysicalToLogicalX(500.0));
        Assert.Equal(500.0, DpiHelper.LogicalToPhysicalX(500.0));
    }

    [Fact]
    public void AsymmetricScale_UsesCorrectAxis()
    {
        DpiHelper.Initialize(2.0, 1.5);
        Assert.Equal(50.0, DpiHelper.PhysicalToLogicalX(100.0));
        Assert.Equal(100.0 / 1.5, DpiHelper.PhysicalToLogicalY(100.0), 5);
        Assert.Equal(200.0, DpiHelper.LogicalToPhysicalX(100.0));
        Assert.Equal(150.0, DpiHelper.LogicalToPhysicalY(100.0));
    }
}
