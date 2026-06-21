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
        Check.That(UIConstants.ActionMenuButtonOuterGap).IsEqualTo(20.0);
    }

    [Fact]
    public void ManualSelectionSideButtonGap_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap).IsEqualTo(20.0);
    }

    [Fact]
    public void ManualSelectionSideButtonDwellMs_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ManualSelectionSideButtonDwellMs > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void ActivationButtonOffset_IsDefinedAndPositive()
    {
        Check.That(UIConstants.ActivationButtonOffset > 0).IsTrue();
        Check.That(UIConstants.ActivationButtonOffset).IsEqualTo(55);
    }

    [Fact]
    public void AllConstants_ArePositive()
    {
        Check.That(UIConstants.ButtonRadius > 0).IsTrue();
        Check.That(UIConstants.ActionMenuButtonOuterGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonGap > 0).IsTrue();
        Check.That(UIConstants.ManualSelectionSideButtonDwellMs > 0).IsTrue();
        Check.That(UIConstants.ActivationButtonOffset > 0).IsTrue();
    }
}
