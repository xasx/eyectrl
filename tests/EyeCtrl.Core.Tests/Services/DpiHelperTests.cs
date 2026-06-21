namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// Tests for DpiHelper coordinate conversions.
/// Part of the non-parallelized DpiHelper Tests collection because it mutates global DpiHelper state.
/// </summary>
[Collection("DpiHelper Tests")]
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
        Check.That(DpiHelper.PhysicalToLogicalX(100.0 * scale)).IsCloseTo(100.0, 1E-05);
        Check.That(DpiHelper.PhysicalToLogicalY(100.0 * scale)).IsCloseTo(100.0, 1E-05);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void LogicalToPhysical_MultipliesByScale(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        Check.That(DpiHelper.LogicalToPhysicalX(100.0)).IsCloseTo(100.0 * scale, 1E-05);
        Check.That(DpiHelper.LogicalToPhysicalY(100.0)).IsCloseTo(100.0 * scale, 1E-05);
    }

    [Fact]
    public void DefaultScale_IsOne()
    {
        DpiHelper.Initialize(1.0, 1.0);
        Check.That(DpiHelper.PhysicalToLogicalX(500.0)).IsEqualTo(500.0);
        Check.That(DpiHelper.LogicalToPhysicalX(500.0)).IsEqualTo(500.0);
    }

    [Fact]
    public void AsymmetricScale_UsesCorrectAxis()
    {
        DpiHelper.Initialize(2.0, 1.5);
        Check.That(DpiHelper.PhysicalToLogicalX(100.0)).IsEqualTo(50.0);
        Check.That(DpiHelper.PhysicalToLogicalY(100.0)).IsCloseTo(100.0 / 1.5, 1E-05);
        Check.That(DpiHelper.LogicalToPhysicalX(100.0)).IsEqualTo(200.0);
        Check.That(DpiHelper.LogicalToPhysicalY(100.0)).IsEqualTo(150.0);
    }
}
