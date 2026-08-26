namespace EyeCtrl.ViewModels.Tests;

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
    public void ActionMenuButtonOuterGap_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ActionMenuButtonOuterGap > 0).IsTrue();
        Check.That(UIConstants.ActionMenuButtonOuterGap).IsEqualTo(60.0);
    }

    [Fact]
    public void ManualSelectionSideButtonGap_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap).IsEqualTo(20.0);
    }

    [Fact]
    public void ActivationButtonOffset_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ActivationButtonOffset > 0).IsTrue();
        Check.That(UIConstants.ActivationButtonOffset).IsEqualTo(55);
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
        Check.That(UIConstants.ActionMenuButtonOuterGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
        Check.That(UIConstants.ActivationButtonOffset > 0).IsTrue();
    }
}
