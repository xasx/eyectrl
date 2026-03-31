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
    public void ZoomSideButtonGap_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ZoomSideButtonGap > 0);
        Assert.Equal(20.0, UIConstants.ZoomSideButtonGap);
    }

    [Fact]
    public void ZoomSideButtonDwellMs_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.ZoomSideButtonDwellMs > 0);
        Assert.Equal(600, UIConstants.ZoomSideButtonDwellMs);
    }

    [Fact]
    public void EmojisPerPage_IsDefinedAndPositive()
    {
        Assert.True(UIConstants.EmojisPerPage > 0);
        Assert.Equal(40, UIConstants.EmojisPerPage);
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
        Assert.True(UIConstants.ZoomSideButtonGap > 0);
        Assert.True(UIConstants.ZoomSideButtonDwellMs > 0);
        Assert.True(UIConstants.EmojisPerPage > 0);
        Assert.True(UIConstants.ActivationButtonOffset > 0);
    }
}
