namespace EyeCtrl.Core.Tests.Keyboard;

using System.Collections.Generic;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// The box-first rule in isolation. The geometry is the case the rule exists for: a WIDE key next
/// to a normal one, where a point inside the wide key near its edge is closer to the neighbour's
/// centre. Nearest-centre gives that point to the neighbour; box-first gives it to the wide key.
///
/// <para>Row (y 0–100): <c>wide</c> spans x 0–240 (centre 120), a 4 px gutter, <c>a</c> spans
/// x 244–344 (centre 294). The point (230, 50) is inside <c>wide</c> but 110 px from its centre
/// and 64 px from <c>a</c>'s.</para>
/// </summary>
public class KeyTargetingTests
{
    private static readonly PhysicalRect WideBox = new(0, 0, 240, 100);
    private static readonly PhysicalRect ABox = new(244, 0, 100, 100);

    private static (Dictionary<string, PhysicalPoint> Centres, Dictionary<string, PhysicalRect> Boxes) Row() =>
        (new() { ["wide"] = WideBox.Center, ["a"] = ABox.Center },
         new() { ["wide"] = WideBox, ["a"] = ABox });

    [Fact]
    public void InsideABox_PicksThatKey_EvenWhenANeighbourCentreIsNearer()
    {
        var (centres, boxes) = Row();
        var gaze = new PhysicalPoint(230, 50);

        // Precondition: the case is real — nearest-centre would pick the neighbour.
        Check.That(gaze.DistanceSquaredTo(centres["a"])).IsStrictlyLessThan(gaze.DistanceSquaredTo(centres["wide"]));

        Check.That(KeyTargeting.PickKey(gaze, centres, boxes)).IsEqualTo("wide");
    }

    [Fact]
    public void WithoutBoxes_IsPlainNearestCentre()
    {
        var (centres, _) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, null)).IsEqualTo("a");
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, new Dictionary<string, PhysicalRect>()))
             .IsEqualTo("a");
    }

    [Theory]
    // In the 4 px gutter between the boxes: nearer a's centre.
    [InlineData(242, 50, "a")]
    // Above the row (an indent / gap band), over the wide key's left half: nearer wide's centre.
    [InlineData(60, -10, "wide")]
    // Above the row, over a: nearer a.
    [InlineData(300, -10, "a")]
    // Below the row near the wide key's right edge: outside every box, nearer a's centre — the
    // gap keeps nearest-centre, it is NOT extended to "nearest box".
    [InlineData(235, 105, "a")]
    public void OutsideEveryBox_PicksTheNearestCentre(double x, double y, string expected)
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(x, y), centres, boxes)).IsEqualTo(expected);
    }

    [Fact]
    public void OutsideEveryBox_StillPicksAKey_FarFromTheGrid()
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(-5000, 9000), centres, boxes)).IsEqualTo("wide");
    }

    [Fact]
    public void Edges_AreInclusive()
    {
        var (centres, boxes) = Row();
        // wide's right edge (x = 240) is nearer a's centre (54 px) than wide's (120 px).
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(240, 50), centres, boxes)).IsEqualTo("wide");
        // a's left edge belongs to a.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(244, 0), centres, boxes)).IsEqualTo("a");
    }

    /// <summary>
    /// Two equal boxes sharing an edge: a point on the edge is inside both and equidistant from
    /// both centres, so the tie-break decides. It is the enumeration (layout) order, the same
    /// strict <c>&lt;</c> the nearest-centre loop always used — deterministic, and pinned both ways
    /// so a "last wins" or ordinal-id tie-break cannot slip in.
    /// </summary>
    [Fact]
    public void SharedEdge_ExactTie_GoesToTheFirstKeyInLayoutOrder()
    {
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(100, 0, 100, 100);
        var edge = new PhysicalPoint(100, 50);

        var lr = KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right });
        var rl = KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["a_right"] = right.Center, ["z_left"] = left.Center },
            new Dictionary<string, PhysicalRect> { ["a_right"] = right, ["z_left"] = left });

        Check.That(lr).IsEqualTo("z_left");
        Check.That(rl).IsEqualTo("a_right");
        // Repeatable for the same input.
        Check.That(KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right })).IsEqualTo(lr);
    }

    /// <summary>
    /// Pass 2 keeps the old loop's tie order too: a gap point exactly between two centres goes to
    /// the key enumerated first, in both orders.
    /// </summary>
    [Fact]
    public void GapPoint_ExactTie_GoesToTheFirstKeyInLayoutOrder()
    {
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(110, 0, 100, 100);
        var gap = new PhysicalPoint(105, 50);   // in the gutter, 55 px from both centres

        Check.That(KeyTargeting.PickKey(gap,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right })).IsEqualTo("z_left");
        Check.That(KeyTargeting.PickKey(gap,
            new Dictionary<string, PhysicalPoint> { ["a_right"] = right.Center, ["z_left"] = left.Center },
            new Dictionary<string, PhysicalRect> { ["a_right"] = right, ["z_left"] = left })).IsEqualTo("a_right");
    }

    /// <summary>
    /// Overlapping boxes: the point is inside both, and the NEAREST centre among them wins — in
    /// both enumeration orders, so it is the distance and not the order deciding. A third key
    /// whose centre is nearer still but whose box does not contain the point must not win.
    /// </summary>
    [Fact]
    public void OverlappingBoxes_ResolveByNearestCentre_AndOutsideKeysDoNotCompete()
    {
        var big = new PhysicalRect(0, 0, 300, 100);      // centre (150, 50)
        var small = new PhysicalRect(200, 0, 100, 100);  // centre (250, 50), inside big
        var outside = new PhysicalRect(305, 0, 20, 100); // centre (315, 50), box excludes the point
        var gaze = new PhysicalPoint(290, 50);           // in big and small; 25 px from outside's centre

        foreach (var order in new[] { new[] { "big", "small", "outside" }, new[] { "outside", "small", "big" } })
        {
            var rects = new Dictionary<string, PhysicalRect> { ["big"] = big, ["small"] = small, ["outside"] = outside };
            var centres = new Dictionary<string, PhysicalPoint>();
            var boxes = new Dictionary<string, PhysicalRect>();
            foreach (var id in order)
            {
                centres[id] = rects[id].Center;
                boxes[id] = rects[id];
            }

            Check.WithCustomMessage($"order {string.Join(",", order)}")
                 .That(KeyTargeting.PickKey(gaze, centres, boxes)).IsEqualTo("small");
        }
    }

    [Fact]
    public void AKeyWithoutABox_CompetesOnlyOnNearestCentre()
    {
        var (centres, boxes) = Row();
        boxes.Remove("wide");
        // Inside wide's (absent) box: no box contains it, so nearest centre — a.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, boxes)).IsEqualTo("a");
        // A box whose key has no centre is ignored entirely.
        var onlyBox = new Dictionary<string, PhysicalRect>(boxes) { ["ghost"] = new(0, 0, 1000, 1000) };
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, onlyBox)).IsEqualTo("a");
    }

    [Fact]
    public void NoKeys_PicksNothing()
    {
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(1, 1),
            new Dictionary<string, PhysicalPoint>(), new Dictionary<string, PhysicalRect>())).IsNull();
    }
}
