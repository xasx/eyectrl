namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Models;
using Xunit;

public class SettingsValidationTests
{
    [Fact]
    public void DefaultSettings_ShouldBeValid()
    {
        // Arrange
        var settings = new EyeCtrlSettings();

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    #region CursorSettings Validation

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void CursorSettings_SmoothingFactor_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new CursorSettings { SmoothingFactor = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.SmoothingFactor)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void CursorSettings_SmoothingFactor_InRange_ShouldPass(double value)
    {
        // Arrange
        var settings = new CursorSettings { SmoothingFactor = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void CursorSettings_SnapRadius_OutOfRange_ShouldFail(int value)
    {
        // Arrange
        var settings = new CursorSettings { SnapRadiusPixels = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.SnapRadiusPixels)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("#FF")]
    [InlineData("#GGGGGG")]
    [InlineData("00FF00")]
    public void CursorSettings_InvalidHexColor_ShouldFail(string value)
    {
        // Arrange
        var settings = new CursorSettings { CircleColor = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.CircleColor)));
    }

    [Theory]
    [InlineData("#00FF00")]
    [InlineData("#FFFFFF")]
    [InlineData("#123abc")]
    public void CursorSettings_ValidHexColor_ShouldPass(string value)
    {
        // Arrange
        var settings = new CursorSettings { CircleColor = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(9.9)]
    [InlineData(501)]
    public void CursorSettings_CircleSize_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new CursorSettings { CircleSize = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.CircleSize)));
    }

    #endregion

    #region DwellSettings Validation

    [Theory]
    [InlineData(99)]
    [InlineData(1001)]
    public void DwellSettings_FixationDetectionMs_OutOfRange_ShouldFail(int value)
    {
        // Arrange
        var settings = new DwellSettings { FixationDetectionMs = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.FixationDetectionMs)));
    }

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void DwellSettings_ActivationButtonDwellMs_OutOfRange_ShouldFail(int value)
    {
        // Arrange
        var settings = new DwellSettings { ActivationButtonDwellMs = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.ActivationButtonDwellMs)));
    }

    [Theory]
    [InlineData(9.9)]
    [InlineData(201)]
    public void DwellSettings_FixationRadiusPixels_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new DwellSettings { FixationRadiusPixels = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.FixationRadiusPixels)));
    }

    #endregion

    #region KeyboardSettings Validation

    [Theory]
    [InlineData("DVORAK")]
    [InlineData("Invalid")]
    [InlineData("")]
    public void KeyboardSettings_InvalidLayout_ShouldFail(string value)
    {
        // Arrange
        var settings = new KeyboardSettings { ActiveLayout = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.ActiveLayout)));
    }

    [Theory]
    [InlineData("QWERTY")]
    [InlineData("AZERTY")]
    [InlineData("QWERTZ")]
    public void KeyboardSettings_ValidLayout_ShouldPass(string value)
    {
        // Arrange
        var settings = new KeyboardSettings { ActiveLayout = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("")]
    public void KeyboardSettings_InvalidScreenEdge_ShouldFail(string value)
    {
        // Arrange
        var settings = new KeyboardSettings { ScreenEdge = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.ScreenEdge)));
    }

    [Theory]
    [InlineData("Top")]
    [InlineData("Bottom")]
    public void KeyboardSettings_ValidScreenEdge_ShouldPass(string value)
    {
        // Arrange
        var settings = new KeyboardSettings { ScreenEdge = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(2.1)]
    public void KeyboardSettings_KeySizeMultiplier_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new KeyboardSettings { KeySizeMultiplier = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.KeySizeMultiplier)));
    }

    #endregion

    #region ToolbarSettings Validation

    [Theory]
    [InlineData("Invalid")]
    [InlineData("Center")]
    [InlineData("")]
    public void ToolbarSettings_InvalidDockedEdge_ShouldFail(string value)
    {
        // Arrange
        var settings = new ToolbarSettings { DockedEdge = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.DockedEdge)));
    }

    [Theory]
    [InlineData("Top")]
    [InlineData("Bottom")]
    [InlineData("Left")]
    [InlineData("Right")]
    public void ToolbarSettings_ValidDockedEdge_ShouldPass(string value)
    {
        // Arrange
        var settings = new ToolbarSettings { DockedEdge = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(201)]
    public void ToolbarSettings_Size_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new ToolbarSettings { Size = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.Size)));
    }

    #endregion

    #region ThemeSettings Validation

    [Theory]
    [InlineData("Invalid")]
    [InlineData("Bright")]
    [InlineData("")]
    public void ThemeSettings_InvalidMode_ShouldFail(string value)
    {
        // Arrange
        var settings = new ThemeSettings { Mode = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.Mode)));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ThemeSettings_ValidMode_ShouldPass(string value)
    {
        // Arrange
        var settings = new ThemeSettings { Mode = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0.09)]
    [InlineData(1.1)]
    public void ThemeSettings_OverlayOpacity_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new ThemeSettings { OverlayOpacity = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.OverlayOpacity)));
    }

    #endregion

    #region ZoomSettings Validation

    [Theory]
    [InlineData(49)]
    [InlineData(501)]
    public void ZoomSettings_CircleRadius_OutOfRange_ShouldFail(int value)
    {
        // Arrange
        var settings = new ZoomSettings { CircleRadiusPixels = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.CircleRadiusPixels)));
    }

    [Theory]
    [InlineData(1.4)]
    [InlineData(5.1)]
    public void ZoomSettings_MagnificationLevel_OutOfRange_ShouldFail(double value)
    {
        // Arrange
        var settings = new ZoomSettings { MagnificationLevel = value };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(nameof(settings.MagnificationLevel)));
    }

    #endregion

    #region Multiple Errors

    [Fact]
    public void CursorSettings_MultipleInvalidValues_ShouldReportAllErrors()
    {
        // Arrange
        var settings = new CursorSettings
        {
            SmoothingFactor = 2.0,  // Invalid
            SnapRadiusPixels = -10,  // Invalid
            CircleColor = "invalid",  // Invalid
            CircleOpacity = 1.5  // Invalid
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void EyeCtrlSettings_InvalidSubSettings_ShouldReportAllErrors()
    {
        // Arrange
        var settings = new EyeCtrlSettings
        {
            Cursor = new CursorSettings { SmoothingFactor = -1 },
            Keyboard = new KeyboardSettings { ActiveLayout = "Invalid" },
            Theme = new ThemeSettings { Mode = "Invalid" }
        };

        // Act
        var errors = settings.Validate();

        // Assert
        Assert.Equal(3, errors.Count);
    }

    #endregion
}
