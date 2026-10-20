namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// The pure geometry behind E1's slip line and saccade class, on the owner's real key geometry:
/// 210 px boxes on a 218 px pitch (an 8 px gap), rows staggered by half a key.
/// </summary>
public class KeyboardE1SlipTests
{
    private const double Pitch = 218;
    private const double Box = 210;

    private static PhysicalRect KeyAt(double col, double row, double rowOffset = 0) =>
        new(rowOffset + col * Pitch, row * Pitch, Box, Box);

    private static PhysicalPoint Centre(PhysicalRect r) => r.Center;

    [Fact]
    public void Pitch_IsTheSmallestDistanceBetweenTwoKeyCentres()
    {
        var centres = new[] { KeyAt(0, 0), KeyAt(1, 0), KeyAt(3, 0), KeyAt(0.5, 1) }.Select(Centre).ToList();

        Check.That(KeyboardE1Slip.Pitch(centres)).IsCloseTo(Pitch, 1e-9);
    }

    [Fact]
    public void Pitch_IsZeroWithFewerThanTwoKeys()
    {
        Check.That(KeyboardE1Slip.Pitch(new[] { new PhysicalPoint(1, 1) })).IsEqualTo(0.0);
        Check.That(KeyboardE1Slip.Pitch(Array.Empty<PhysicalPoint>())).IsEqualTo(0.0);
    }

    [Theory]
    [InlineData(0.0, "short")]
    [InlineData(108.9, "short")]      // just under half a pitch
    [InlineData(109.0, "mid")]        // exactly half a pitch
    [InlineData(218.0, "mid")]
    [InlineData(436.0, "mid")]        // exactly two pitches
    [InlineData(436.1, "long")]
    [InlineData(2000.0, "long")]
    public void SaccadeClass_BucketsByKeyPitch(double px, string expected)
    {
        Check.That(KeyboardE1Slip.SaccadeClass(px, Pitch)).IsEqualTo(expected);
    }

    [Fact]
    public void SaccadeClass_WithoutAPitch_IsUnknown()
    {
        Check.That(KeyboardE1Slip.SaccadeClass(500, 0)).IsEqualTo("-");
    }

    [Fact]
    public void Adjacent_SideBySideKeys_AreNeighbours()
    {
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(2, 0), KeyAt(3, 0), Pitch)).IsTrue();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(2, 0), KeyAt(2, 1), Pitch)).IsTrue();
    }

    [Fact]
    public void Adjacent_DiagonalContact_IsANeighbour()
    {
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(2, 1), KeyAt(3, 2), Pitch)).IsTrue();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(2, 1), KeyAt(1, 0), Pitch)).IsTrue();
    }

    [Fact]
    public void Adjacent_TheHalfKeyStaggerBetweenRows_StillTouches()
    {
        // The owner's grid: row 1 sits half a key to the LEFT of row 0.
        var q = KeyAt(0, 0);
        var a = KeyAt(0, 1, rowOffset: -Pitch / 2);
        var s = KeyAt(1, 1, rowOffset: -Pitch / 2);

        Check.That(KeyboardE1Slip.Adjacent(q, a, Pitch)).IsTrue();
        Check.That(KeyboardE1Slip.Adjacent(q, s, Pitch)).IsTrue();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(2, 0), a, Pitch)).IsFalse();
    }

    [Fact]
    public void Adjacent_AKeyAwayOrTwoApart_IsNot()
    {
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(0, 0), KeyAt(2, 0), Pitch)).IsFalse();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(0, 0), KeyAt(0, 2), Pitch)).IsFalse();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(0, 0), KeyAt(2, 2), Pitch)).IsFalse();
        Check.That(KeyboardE1Slip.Adjacent(KeyAt(0, 0), KeyAt(1, 2), Pitch)).IsFalse();
    }

    [Theory]
    [InlineData(1, 0, "E")]
    [InlineData(1, -1, "NE")]
    [InlineData(0, -1, "N")]
    [InlineData(-1, -1, "NW")]
    [InlineData(-1, 0, "W")]
    [InlineData(-1, 1, "SW")]
    [InlineData(0, 1, "S")]
    [InlineData(1, 1, "SE")]
    public void Direction_IsEightWay_WithScreenUpAsNorth(double dx, double dy, string expected)
    {
        var from = new PhysicalPoint(500, 500);

        Check.That(KeyboardE1Slip.Direction(from, new PhysicalPoint(500 + dx * Pitch, 500 + dy * Pitch)))
            .IsEqualTo(expected);
    }

    [Fact]
    public void Direction_ReadsTheNearestCompassPoint_ForTheStaggeredRows()
    {
        // Half a key across and one row down is 63 degrees below east: south-east, not south.
        var from = new PhysicalPoint(500, 500);
        Check.That(KeyboardE1Slip.Direction(from, new PhysicalPoint(500 + Pitch / 2, 500 + Pitch))).IsEqualTo("SE");
        Check.That(KeyboardE1Slip.Direction(from, new PhysicalPoint(500 + Pitch * 0.2, 500 + Pitch))).IsEqualTo("S");
        Check.That(KeyboardE1Slip.Direction(from, new PhysicalPoint(500 + Pitch, 500 + Pitch * 0.6))).IsEqualTo("SE");
        Check.That(KeyboardE1Slip.Direction(from, new PhysicalPoint(500 - Pitch, 500 - Pitch * 0.1))).IsEqualTo("W");
    }

    [Fact]
    public void Direction_OfTheSamePoint_IsNone()
    {
        Check.That(KeyboardE1Slip.Direction(new PhysicalPoint(1, 2), new PhysicalPoint(1, 2))).IsEqualTo("-");
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(218.0, 1)]
    [InlineData(300.0, 1)]
    [InlineData(330.0, 2)]     // 1.51 pitches rounds to 2
    [InlineData(654.0, 3)]
    [InlineData(5000.0, 3)]    // capped
    public void DistancePitches_IsRoundedAndCappedAtThree(double px, int expected)
    {
        Check.That(KeyboardE1Slip.DistancePitches(new PhysicalPoint(0, 0), new PhysicalPoint(px, 0), Pitch))
            .IsEqualTo(expected);
    }

    [Theory]
    [InlineData(0, 4, "top")]
    [InlineData(1, 4, "top")]
    [InlineData(2, 4, "mid")]
    [InlineData(3, 4, "bot")]
    [InlineData(0, 3, "top")]
    [InlineData(1, 3, "mid")]
    [InlineData(2, 3, "bot")]
    [InlineData(0, 1, "top")]
    [InlineData(-1, 4, "-")]
    [InlineData(4, 4, "-")]
    [InlineData(0, 0, "-")]
    public void RowBand_IsThirdsOfTheSubLayoutsRows(int row, int rowCount, string expected)
    {
        Check.That(KeyboardE1Slip.RowBand(row, rowCount)).IsEqualTo(expected);
    }
}
