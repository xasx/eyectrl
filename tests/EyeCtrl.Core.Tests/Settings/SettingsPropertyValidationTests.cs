namespace EyeCtrl.Core.Tests.Settings;

using System.ComponentModel;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// Tests for real-time property validation using MVVM Toolkit OnChanging partial methods.
/// These tests verify that validation happens at the setter level, not just in Validate() method.
///
/// TDD RED Phase: These tests will FAIL until OnChanging partial methods are implemented.
/// </summary>
public class SettingsPropertyValidationTests
{
    #region DwellSettings - FixationDetectionMs Validation

    [Fact]
    public void FixationDetectionMs_BelowMinimum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationDetectionMs = 99;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("100", exception.Message);
        Assert.Contains("1000", exception.Message);
        Assert.Contains("FixationDetectionMs", exception.Message);
    }

    [Fact]
    public void FixationDetectionMs_AboveMaximum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationDetectionMs = 1001;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("100", exception.Message);
        Assert.Contains("1000", exception.Message);
        Assert.Contains("FixationDetectionMs", exception.Message);
    }

    [Theory]
    [InlineData(100)]  // Minimum boundary
    [InlineData(300)]  // Valid middle value
    [InlineData(1000)] // Maximum boundary
    public void FixationDetectionMs_ValidValue_AllowsChange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.FixationDetectionMs = value;

        // Assert
        Assert.Equal(value, settings.FixationDetectionMs);
    }

    [Theory]
    [InlineData(100)]  // Minimum boundary
    [InlineData(1000)] // Maximum boundary
    public void FixationDetectionMs_BoundaryValues_AllowsChange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.FixationDetectionMs = value;

        // Assert
        Assert.Equal(value, settings.FixationDetectionMs);
    }

    #endregion

    #region DwellSettings - ActivationButtonDwellMs Validation

    [Fact]
    public void ActivationButtonDwellMs_BelowMinimum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ActivationButtonDwellMs = 199;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("200", exception.Message);
        Assert.Contains("2000", exception.Message);
    }

    [Fact]
    public void ActivationButtonDwellMs_AboveMaximum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ActivationButtonDwellMs = 2001;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("200", exception.Message);
        Assert.Contains("2000", exception.Message);
    }

    [Fact]
    public void ActivationButtonDwellMs_InvalidValue_ThrowsWithCorrectMessage()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ActivationButtonDwellMs = 50;
        });

        Assert.Contains("ActivationButtonDwellMs", exception.Message);
        Assert.Contains("must be between", exception.Message);
    }

    [Theory]
    [InlineData(200)]  // Minimum boundary
    [InlineData(700)]  // Valid middle value
    [InlineData(2000)] // Maximum boundary
    public void ActivationButtonDwellMs_ValidValue_AllowsChange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.ActivationButtonDwellMs = value;

        // Assert
        Assert.Equal(value, settings.ActivationButtonDwellMs);
    }

    #endregion

    #region DwellSettings - ActionMenuDwellMs Validation

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void ActionMenuDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ActionMenuDwellMs = value;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("200", exception.Message);
        Assert.Contains("2000", exception.Message);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(2000)]
    public void ActionMenuDwellMs_ValidValue_AllowsChange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.ActionMenuDwellMs = value;

        // Assert
        Assert.Equal(value, settings.ActionMenuDwellMs);
    }

    #endregion

    #region DwellSettings - ZoomActionMenuDwellMs Validation

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void ZoomActionMenuDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ZoomActionMenuDwellMs = value;
        });

        Assert.Equal("value", exception.ParamName);
    }

    #endregion

    #region DwellSettings - ToolbarButtonDwellMs Validation

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void ToolbarButtonDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ToolbarButtonDwellMs = value;
        });
    }

    #endregion

    #region DwellSettings - KeyboardKeyDwellMs Validation

    [Theory]
    [InlineData(99)]
    [InlineData(1501)]
    public void KeyboardKeyDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.KeyboardKeyDwellMs = value;
        });

        Assert.Contains("100", exception.Message);
        Assert.Contains("1500", exception.Message);
    }

    #endregion

    #region DwellSettings - KeyboardPredictionDwellMs Validation

    [Theory]
    [InlineData(99)]
    [InlineData(1501)]
    public void KeyboardPredictionDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.KeyboardPredictionDwellMs = value;
        });
    }

    #endregion

    #region DwellSettings - FixationRadiusPixels Validation

    [Theory]
    [InlineData(9.9)]
    [InlineData(200.1)]
    public void FixationRadiusPixels_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationRadiusPixels = value;
        });

        Assert.Contains("10", exception.Message);
        Assert.Contains("200", exception.Message);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(50.0)]
    [InlineData(200.0)]
    public void FixationRadiusPixels_ValidValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.FixationRadiusPixels = value;

        // Assert
        Assert.Equal(value, settings.FixationRadiusPixels);
    }

    #endregion

    #region DwellSettings - VelocityThresholdPxPerSec Validation

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0)]
    public void VelocityThresholdPxPerSec_NegativeOrZero_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.VelocityThresholdPxPerSec = value;
        });

        Assert.Contains("must be greater than 0", exception.Message);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(100.0)]
    [InlineData(500.0)]
    public void VelocityThresholdPxPerSec_PositiveValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act
        settings.VelocityThresholdPxPerSec = value;

        // Assert
        Assert.Equal(value, settings.VelocityThresholdPxPerSec);
    }

    #endregion

    #region CursorSettings - SmoothingFactor Validation

    [Fact]
    public void SmoothingFactor_BelowZero_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.SmoothingFactor = -0.1;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("0", exception.Message);
        Assert.Contains("1", exception.Message);
        Assert.Contains("SmoothingFactor", exception.Message);
    }

    [Fact]
    public void SmoothingFactor_AboveOne_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.SmoothingFactor = 1.1;
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("0", exception.Message);
        Assert.Contains("1", exception.Message);
    }

    [Theory]
    [InlineData(0.0)]  // Minimum boundary
    [InlineData(0.5)]  // Valid middle value
    [InlineData(1.0)]  // Maximum boundary
    public void SmoothingFactor_ValidValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act
        settings.SmoothingFactor = value;

        // Assert
        Assert.Equal(value, settings.SmoothingFactor);
    }

    #endregion

    #region CursorSettings - OneEuroMinCutoff Validation

    [Theory]
    [InlineData(0.05)]
    [InlineData(20.1)]
    public void OneEuroMinCutoff_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.OneEuroMinCutoff = value;
        });

        Assert.Contains("0.1", exception.Message);
        Assert.Contains("20", exception.Message);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void OneEuroMinCutoff_ValidValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act
        settings.OneEuroMinCutoff = value;

        // Assert
        Assert.Equal(value, settings.OneEuroMinCutoff);
    }

    #endregion

    #region CursorSettings - OneEuroBeta Validation

    [Theory]
    [InlineData(-0.1)]
    [InlineData(5.1)]
    public void OneEuroBeta_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.OneEuroBeta = value;
        });

        Assert.Contains("0", exception.Message);
        Assert.Contains("5", exception.Message);
    }

    #endregion

    #region CursorSettings - SnapRadiusPixels Validation

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SnapRadiusPixels_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.SnapRadiusPixels = value;
        });

        Assert.Contains("0", exception.Message);
        Assert.Contains("100", exception.Message);
    }

    #endregion

    #region CursorSettings - CircleColor Validation

    [Fact]
    public void CircleColor_InvalidHex_ThrowsArgumentException()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
        {
            settings.CircleColor = "invalid";
        });

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("CircleColor", exception.Message);
        Assert.Contains("hex color", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FF")]
    [InlineData("#GGGGGG")]
    [InlineData("00FF00")]
    public void CircleColor_VariousInvalidFormats_ThrowsArgumentException(string value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
        {
            settings.CircleColor = value;
        });
    }

    [Theory]
    [InlineData("#00FF00")]
    [InlineData("#FFFFFF")]
    [InlineData("#123abc")]
    [InlineData("#000000")]
    public void CircleColor_ValidHex_AllowsChange(string value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act
        settings.CircleColor = value;

        // Assert
        Assert.Equal(value, settings.CircleColor);
    }

    #endregion

    #region CursorSettings - CircleOpacity Validation

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void CircleOpacity_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.CircleOpacity = value;
        });

        Assert.Contains("0", exception.Message);
        Assert.Contains("1", exception.Message);
    }

    #endregion

    #region CursorSettings - CircleThickness Validation

    [Theory]
    [InlineData(0.9)]
    [InlineData(10.1)]
    public void CircleThickness_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.CircleThickness = value;
        });

        Assert.Contains("1", exception.Message);
        Assert.Contains("10", exception.Message);
    }

    #endregion

    #region CursorSettings - CircleSize Validation

    [Theory]
    [InlineData(9.9)]
    [InlineData(500.1)]
    public void CircleSize_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.CircleSize = value;
        });

        Assert.Contains("10", exception.Message);
        Assert.Contains("500", exception.Message);
    }

    #endregion

    #region ZoomSettings - CircleRadiusPixels Validation

    [Theory]
    [InlineData(49)]
    [InlineData(501)]
    public void ZoomSettings_CircleRadiusPixels_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new ZoomSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.CircleRadiusPixels = value;
        });

        Assert.Contains("50", exception.Message);
        Assert.Contains("500", exception.Message);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    [InlineData(500)]
    public void ZoomSettings_CircleRadiusPixels_ValidValue_AllowsChange(int value)
    {
        // Arrange
        var settings = new ZoomSettings();

        // Act
        settings.CircleRadiusPixels = value;

        // Assert
        Assert.Equal(value, settings.CircleRadiusPixels);
    }

    #endregion

    #region ZoomSettings - MagnificationLevel Validation

    [Theory]
    [InlineData(1.4)]
    [InlineData(5.1)]
    public void ZoomSettings_MagnificationLevel_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new ZoomSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.MagnificationLevel = value;
        });

        Assert.Contains("1.5", exception.Message);
        Assert.Contains("5", exception.Message);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void ZoomSettings_MagnificationLevel_ValidValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new ZoomSettings();

        // Act
        settings.MagnificationLevel = value;

        // Assert
        Assert.Equal(value, settings.MagnificationLevel);
    }

    #endregion

    #region General Validation Patterns

    [Fact]
    public void OnChanging_InvalidValue_DoesNotChangeProperty()
    {
        // Arrange
        var settings = new DwellSettings { FixationDetectionMs = 300 };

        // Act & Assert - attempt to set invalid value
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationDetectionMs = 50;
        });

        // Property should retain original value
        Assert.Equal(300, settings.FixationDetectionMs);
    }

    [Fact]
    public void OnChanging_ValidValue_FiresPropertyChanged()
    {
        // Arrange
        var settings = new DwellSettings();
        var propertyChangedFired = false;
        string? changedPropertyName = null;

        settings.PropertyChanged += (sender, args) =>
        {
            propertyChangedFired = true;
            changedPropertyName = args.PropertyName;
        };

        // Act
        settings.FixationDetectionMs = 500;

        // Assert
        Assert.True(propertyChangedFired);
        Assert.Equal(nameof(settings.FixationDetectionMs), changedPropertyName);
    }

    [Fact]
    public void MultipleProperties_ValidValues_AllSucceed()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act - set multiple valid properties
        settings.FixationDetectionMs = 400;
        settings.ActivationButtonDwellMs = 800;
        settings.ActionMenuDwellMs = 700;
        settings.FixationRadiusPixels = 75.0;

        // Assert
        Assert.Equal(400, settings.FixationDetectionMs);
        Assert.Equal(800, settings.ActivationButtonDwellMs);
        Assert.Equal(700, settings.ActionMenuDwellMs);
        Assert.Equal(75.0, settings.FixationRadiusPixels);
    }

    [Fact]
    public void MultipleProperties_MixedValidAndInvalid_OnlyInvalidThrows()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act - set valid value
        settings.FixationDetectionMs = 400;
        Assert.Equal(400, settings.FixationDetectionMs);

        // Act & Assert - attempt invalid value
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.ActivationButtonDwellMs = 50;
        });

        // First property should remain changed
        Assert.Equal(400, settings.FixationDetectionMs);
    }

    [Fact]
    public void PropertyChanged_OnlyFiresForActualChanges()
    {
        // Arrange
        var settings = new DwellSettings { FixationDetectionMs = 300 };
        var fireCount = 0;

        settings.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(settings.FixationDetectionMs))
            {
                fireCount++;
            }
        };

        // Act - set to same value
        settings.FixationDetectionMs = 300;

        // Assert - PropertyChanged should not fire for unchanged values
        Assert.Equal(0, fireCount);

        // Act - set to different value
        settings.FixationDetectionMs = 400;

        // Assert - PropertyChanged should fire once
        Assert.Equal(1, fireCount);
    }

    #endregion

    #region Exception Message Quality

    [Fact]
    public void ValidationException_ContainsPropertyName()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.SmoothingFactor = -1;
        });

        Assert.Contains("SmoothingFactor", exception.Message);
    }

    [Fact]
    public void ValidationException_ContainsValidRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationDetectionMs = 50;
        });

        Assert.Contains("100", exception.Message);
        Assert.Contains("1000", exception.Message);
    }

    [Fact]
    public void ValidationException_ContainsActualValue()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            settings.FixationDetectionMs = 5000;
        });

        Assert.Contains("5000", exception.Message);
    }

    #endregion

    #region Thread Safety and Performance

    [Fact]
    public void Validation_PerformsQuickly()
    {
        // Arrange - Run the test multiple times to reduce flakiness from GC/system load
        const int iterations = 5;
        var timings = new List<long>();

        for (int run = 0; run < iterations; run++)
        {
            var settings = new DwellSettings();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Act - perform 1000 validations
            for (int i = 0; i < 1000; i++)
            {
                settings.FixationDetectionMs = 300 + (i % 100);
            }

            stopwatch.Stop();
            timings.Add(stopwatch.ElapsedMilliseconds);
        }

        // Assert - use median time to reduce impact of outliers (more reliable than single run)
        var sortedTimings = timings.OrderBy(t => t).ToList();
        var medianTime = sortedTimings[iterations / 2];

        Assert.True(medianTime < 100,
            $"Median validation time was {medianTime}ms (runs: {string.Join(", ", timings)}ms). Expected < 100ms for 1000 operations.");
    }

    #endregion
}
