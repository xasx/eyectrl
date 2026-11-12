namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core;
using EyeCtrl.ViewModels;

/// <summary>
/// Tests to verify UI constants are defined and have expected values.
/// These tests ensure magic numbers have been properly extracted to named constants.
/// </summary>
public class UIConstantsTests
{
    [Fact]
    public void ButtonRadius_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ButtonRadius > 0).IsTrue();
        Check.That(UIConstants.ButtonRadius).IsEqualTo(50.0);
    }

    [Fact]
    public void ManualSelectionSideButtonGap_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap).IsEqualTo(20.0);
    }

    [Fact]
    public void ActivationButtonOffset_LivesInLayoutConstants_AndIsTheOwnersOptionB()
    {
        // Ticket 7 (N9): Core's placement function owns the number. 137.5 DIP since the 7th sitting
        // (2026-11-12, the owner's option B: 475 px from P at ring 200 and 200 %); was 55. The ViewModels
        // int alias was dropped then: (int)137.5 would have read 137, and only this test read it.
        Check.That(LayoutConstants.ActivationButtonOffset).IsEqualTo(137.5);
        Check.That(typeof(UIConstants).GetField("ActivationButtonOffset")).IsNull();
    }

    [Fact]
    public void ActivationButtonHitRadius_IsItsOwnPhysicalConstant()
    {
        // Ticket 7 (N4): the dwell target is physical pixels and its own constant. Mutation: bump or
        // reuse SharedButtonRadius for it → red on the size that roughly ten other buttons read. 200 px
        // since F2 (5th sitting, 2026-11-09: the owner's answer Q-hit = a; was 150); 265 px since the 7th
        // sitting (2026-11-12, the owner's option B, with the 475 px offer).
        Check.That(LayoutConstants.ActivationButtonHitRadiusPx).IsEqualTo(265.0);
        Check.That(LayoutConstants.SharedButtonRadius).IsEqualTo(50.0);
        Check.That(LayoutConstants.ActivationButtonHitRadiusPx)
            .IsNotEqualTo(LayoutConstants.SharedButtonRadius);
    }

    /// <summary>
    /// The action menu's button radius and the radius every OTHER circular overlay uses are
    /// separate constants, and must stay separate.
    ///
    /// They were one constant until 2026-08-26: <c>UIConstants.ButtonRadius</c> aliased
    /// <c>LayoutConstants.ActionMenuButtonRadius</c>, and eight surfaces that are not the action
    /// menu read it — the activation button, the backdrop hide button, the ManualSelection side
    /// buttons, the scroll-stop button, the quick-action grid and the task switcher. Growing the
    /// action-menu target to 140 DIP through that alias would have grown all of them, hit-targets
    /// included, for a user whose only input channel is an eye tracker.
    ///
    /// This test fails if someone re-aliases them, which is the specific regression that would
    /// otherwise be invisible: every existing test still passes when the shared radius silently
    /// grows, because none of those eight surfaces pins its own size.
    /// </summary>
    [Fact]
    public void ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius()
    {
        Check.That(UIConstants.ButtonRadius).IsEqualTo(50.0);
        Check.That(UIConstants.ActionMenuButtonRadius).IsEqualTo(70.0);
        Check.That(UIConstants.ButtonRadius).IsNotEqualTo(UIConstants.ActionMenuButtonRadius);
    }

    [Fact]
    public void AllConstants_ArePositive()
    {
        Check.That(UIConstants.ButtonRadius > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
    }
}
