namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Reflection;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The ratios are not theme tokens: they describe a glyph's size relative to the container
/// that bounds it, which no palette changes. They are pinned here because every value was
/// taken from a site that already used it (design spec §8.5) — if one drifts, the site it
/// came from has silently changed shape.
/// </summary>
public class ContainerRatioTests
{
    [Fact]
    public void Ratios_HoldTheValuesTheirSitesObserved()
    {
        Check.That(ContainerRatio.Badge).IsEqualTo(0.60);      // DragTrail 16/26 = 0.615
        Check.That(ContainerRatio.Dominant).IsEqualTo(0.45);   // QuickActions close 20/44 = 0.455
        Check.That(ContainerRatio.Large).IsEqualTo(0.40);      // Toolbar's existing 0.4; emoji 28/70
        Check.That(ContainerRatio.Medium).IsEqualTo(0.32);     // category 22/70; skin-tone 36/105
        Check.That(ContainerRatio.Small).IsEqualTo(0.23);      // tile 28/120; key 16/70; note 14/60
    }

    [Fact]
    public void Ratios_DescendAndStayWithinSaneBounds()
    {
        Check.That(ContainerRatio.Badge).IsStrictlyGreaterThan(ContainerRatio.Dominant);
        Check.That(ContainerRatio.Dominant).IsStrictlyGreaterThan(ContainerRatio.Large);
        Check.That(ContainerRatio.Large).IsStrictlyGreaterThan(ContainerRatio.Medium);
        Check.That(ContainerRatio.Medium).IsStrictlyGreaterThan(ContainerRatio.Small);
        Check.That(ContainerRatio.Small).IsStrictlyGreaterThan(0.0);
        Check.That(ContainerRatio.Badge).IsStrictlyLessThan(1.0);
    }

    [Fact]
    public void AppliedToTheirSites_TheRatiosReproduceTodaysSizes()
    {
        // Each cell is "container size x ratio ~= the literal it replaces". These are the
        // deltas the spec's §8.5 table commits to; if one drifts the visual change is larger
        // than the design allowed for.
        Check.That(26 * ContainerRatio.Badge).IsCloseTo(16.0, 0.5);     // 15.6
        Check.That(44 * ContainerRatio.Dominant).IsCloseTo(20.0, 0.5);  // 19.8
        Check.That(70 * ContainerRatio.Large).IsCloseTo(28.0, 0.5);     // 28.0
        Check.That(105 * ContainerRatio.Medium).IsCloseTo(36.0, 2.5);   // 33.6, the -2.4 outlier
        Check.That(70 * ContainerRatio.Small).IsCloseTo(16.0, 0.5);     // 16.1
        Check.That(120 * ContainerRatio.Small).IsCloseTo(28.0, 0.5);    // 27.6
        Check.That(60 * ContainerRatio.Small).IsCloseTo(14.0, 0.5);     // 13.8
        Check.That(100 * ContainerRatio.Small).IsCloseTo(22.0, 1.5);    // 23.0
    }

    [Fact]
    public void EveryRatio_JoinsTheOrderedChainAndIsDistinct()
    {
        // The hand-written chain above only covers the five that exist today. This makes a
        // sixth ratio declare itself: it must be distinct, inside (0, 1), and in descending
        // order. Spec §8.4 rejected a duplicate-valued token as "the same fragmentation in a
        // different coat"; that rule applies to ratios too, and nothing else enforces it.
        var values = typeof(ContainerRatio)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (double)f.GetRawConstantValue()!)
            .ToList();

        Check.WithCustomMessage("Reflected no ratios — wrong BindingFlags or they stopped being const.")
            .That(values).Not.IsEmpty();
        Check.WithCustomMessage("Two ratios holding one value is fragmentation in a different coat.")
            .That(values).ContainsNoDuplicateItem();
        Check.That(values).ContainsExactly(values.OrderByDescending(v => v));
        foreach (var v in values)
        {
            Check.That(v).IsStrictlyGreaterThan(0.0);
            Check.That(v).IsStrictlyLessThan(1.0);
        }
    }
}
