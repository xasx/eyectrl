namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

public class KeyboardYieldTests
{
    // Screen ≈ 1920x1040 DIP work area; keyboard 1000x400 docked Bottom (Top = 640) or Top
    // (Top = 0); toolbar extent 132 DIP. Same-edge yield = one toolbar height toward center.

    [Fact]
    public void NoYield_WhenToolbarAbsent_OrEdgeDiffers()
    {
        Check.That(KeyboardYield.VerticalOffset("bottom", false, "bottom", 132, 640, 400, 0, 1040)).IsEqualTo(0);
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "top", 132, 640, 400, 0, 1040)).IsEqualTo(0);
        Check.That(KeyboardYield.VerticalOffset("top", true, "left", 132, 0, 400, 0, 1040)).IsEqualTo(0);
    }

    [Fact]
    public void TopEdge_YieldsDown_ByExtent()
    {
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", 132, 0, 400, 0, 1040)).IsEqualTo(132);
    }

    [Fact]
    public void BottomEdge_YieldsUp_ByExtent()
    {
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "bottom", 132, 640, 400, 0, 1040)).IsEqualTo(-132);
    }

    [Fact]
    public void CaseInsensitiveEdgeComparison()
    {
        Check.That(KeyboardYield.VerticalOffset("Top", true, "TOP", 132, 0, 400, 0, 1040)).IsEqualTo(132);
    }

    [Fact]
    public void Clamps_ExtremeExtent_ToKeepKeyboardInWorkArea()
    {
        // A pathological toolbar measurement must not push the keyboard off-screen (spec B3 #6):
        // top-docked keyboard, absurd 5000 DIP extent → clamped to (workAreaBottom - height - top).
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", 5000, 0, 400, 0, 1040)).IsEqualTo(640);

        // Bottom-docked, absurd extent → clamped to work-area top.
        Check.That(KeyboardYield.VerticalOffset("bottom", true, "bottom", 5000, 640, 400, 0, 1040)).IsEqualTo(-640);
    }

    [Fact]
    public void NegativeExtent_IsClampedToZero()
    {
        Check.That(KeyboardYield.VerticalOffset("top", true, "top", -50, 0, 400, 0, 1040)).IsEqualTo(0);
    }
}
