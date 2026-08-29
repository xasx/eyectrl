namespace EyeCtrl.Controls.Tests.Icons;

using System;
using EyeCtrl.Controls.Icons;
using NFluent;
using Xunit;

/// <summary>
/// Pins the one invariant <see cref="IconGeometry.FromPathData"/> enforces at construction: the
/// result always has at least one layer. Everything else about "frozen" and "non-degenerate" is
/// still a per-registry test, not a construction guarantee — <see cref="IconGeometry"/>'s own
/// positional constructor happily accepts unfrozen geometry, or none at all.
/// </summary>
public class IconGeometryTests
{
    [Fact]
    public void FromPathData_BothNull_Throws()
    {
        Check.ThatCode(() => IconGeometry.FromPathData(null))
             .Throws<ArgumentException>();
    }
}
