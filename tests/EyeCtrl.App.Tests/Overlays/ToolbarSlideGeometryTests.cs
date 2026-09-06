namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;

/// <summary>
/// <see cref="OverlayPositioning.SlideVectorFor"/> is the entire direction table of the toolbar's
/// slide transition, and it is pure — no window, no dispatcher, no STA thread. Plain [Fact]s for
/// the same reason <see cref="OverlayPositioningTests"/> are: nothing here touches WPF beyond a
/// struct, so these run in CI's headless subset (.github/workflows/ci.yml:84 filters Category!=UI).
/// </summary>
public class ToolbarSlideGeometryTests
{
    private const double W = 900;
    private const double H = 140;

    [Fact]
    public void TopDock_RetractsUpward_ByTheFullHeight()
    {
        var v = OverlayPositioning.SlideVectorFor("top", W, H);
        Check.That(v.X).IsEqualTo(0d);
        Check.That(v.Y).IsEqualTo(-H);
    }

    [Fact]
    public void BottomDock_RetractsDownward_ByTheFullHeight()
    {
        var v = OverlayPositioning.SlideVectorFor("bottom", W, H);
        Check.That(v.X).IsEqualTo(0d);
        Check.That(v.Y).IsEqualTo(H);
    }

    [Fact]
    public void LeftDock_RetractsLeftward_ByTheFullWidth()
    {
        var v = OverlayPositioning.SlideVectorFor("left", W, H);
        Check.That(v.X).IsEqualTo(-W);
        Check.That(v.Y).IsEqualTo(0d);
    }

    [Fact]
    public void RightDock_RetractsRightward_ByTheFullWidth()
    {
        var v = OverlayPositioning.SlideVectorFor("right", W, H);
        Check.That(v.X).IsEqualTo(W);
        Check.That(v.Y).IsEqualTo(0d);
    }

    /// <summary>
    /// PascalCase is what ToolbarSettings.DockedEdge actually stores ("Top"/"Bottom"/"Left"/
    /// "Right"), so case-insensitivity is a real requirement, not politeness — the same reason
    /// OverlayPositioningTests.EdgeCase_IsInsensitive_AndPascalCaseMatches exists.
    /// </summary>
    [Fact]
    public void EdgeMatching_IsCaseInsensitive()
    {
        Check.That(OverlayPositioning.SlideVectorFor("Top", W, H).Y).IsEqualTo(-H);
        Check.That(OverlayPositioning.SlideVectorFor("RIGHT", W, H).X).IsEqualTo(W);
    }

    /// <summary>
    /// Null is the UNDOCKED case and must NOT fall back to "top" the way PlaceAtEdge does: a
    /// floating toolbar has no edge to retract into, and animating it upward would be a bug.
    /// This asymmetry with its neighbour in the same class is deliberate.
    /// </summary>
    [Fact]
    public void NullEdge_MeansNoSlide_NotTop()
    {
        Check.That(OverlayPositioning.SlideVectorFor(null, W, H)).IsEqualTo(default(System.Windows.Vector));
    }

    [Fact]
    public void UnrecognisedEdge_MeansNoSlide()
    {
        Check.That(OverlayPositioning.SlideVectorFor("diagonal", W, H)).IsEqualTo(default(System.Windows.Vector));
    }

    /// <summary>
    /// An unmeasured window (SizeToContent, never laid out) reports zero extent. A zero vector is
    /// the graceful answer — callers degrade to an instant show/hide rather than special-casing it.
    /// </summary>
    [Fact]
    public void ZeroExtent_MeansNoSlide()
    {
        Check.That(OverlayPositioning.SlideVectorFor("top", 0, 0)).IsEqualTo(default(System.Windows.Vector));
    }
}
