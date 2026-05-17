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
        Assert.True(UIConstants.ButtonRadius > 0);
        Assert.Equal(50.0, UIConstants.ButtonRadius);
    }

    [Fact]
    public void ActionMenuButtonOuterGap_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ActionMenuButtonOuterGap > 0);
        Assert.Equal(20.0, UIConstants.ActionMenuButtonOuterGap);
    }

    [Fact]
    public void ManualSelectionSideButtonGap_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ManualSelectionSideButtonGap > 0);
        Assert.Equal(20.0, UIConstants.ManualSelectionSideButtonGap);
    }

    [Fact]
    public void ManualSelectionSideButtonDwellMs_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ManualSelectionSideButtonDwellMs > 0);
        Assert.Equal(600, UIConstants.ManualSelectionSideButtonDwellMs);
    }

    [Fact]
    public void ActivationButtonOffset_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ActivationButtonOffset > 0);
        Assert.Equal(55, UIConstants.ActivationButtonOffset);
    }

    [Fact]
    public void AllConstants_ArePositive()
    {
        // Verify all numeric constants are positive where appropriate
        Assert.True(UIConstants.ButtonRadius > 0);
        Assert.True(UIConstants.ActionMenuButtonOuterGap > 0);
        Assert.True(UIConstants.ManualSelectionSideButtonGap > 0);
        Assert.True(UIConstants.ManualSelectionSideButtonDwellMs > 0);
        Assert.True(UIConstants.ActivationButtonOffset > 0);
    }
}
