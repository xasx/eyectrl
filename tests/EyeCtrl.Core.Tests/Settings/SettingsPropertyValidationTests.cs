namespace EyeCtrl.Core.Tests.Settings;

using System.ComponentModel;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// Tests for real-time property validation using MVVM Toolkit OnChanging partial methods.
/// These tests verify that validation happens at the setter level, not just in Validate() method.
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.FixationDetectionMs = 99; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("100");
        Check.That(ex.Message).Contains("1000");
        Check.That(ex.Message).Contains("FixationDetectionMs");
    }

    [Fact]
    public void FixationDetectionMs_AboveMaximum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.FixationDetectionMs = 1001; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("100");
        Check.That(ex.Message).Contains("1000");
        Check.That(ex.Message).Contains("FixationDetectionMs");
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
        Check.That(settings.FixationDetectionMs).IsEqualTo(value);
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
        Check.That(settings.FixationDetectionMs).IsEqualTo(value);
    }

    #endregion

    #region DwellSettings - ActivationButtonDwellMs Validation

    [Fact]
    public void ActivationButtonDwellMs_BelowMinimum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.ActivationButtonDwellMs = 199; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("200");
        Check.That(ex.Message).Contains("2000");
    }

    [Fact]
    public void ActivationButtonDwellMs_AboveMaximum_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.ActivationButtonDwellMs = 2001; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("200");
        Check.That(ex.Message).Contains("2000");
    }

    [Fact]
    public void ActivationButtonDwellMs_InvalidValue_ThrowsWithCorrectMessage()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.ActivationButtonDwellMs = 50; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("ActivationButtonDwellMs");
        Check.That(ex.Message).Contains("must be between");
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
        Check.That(settings.ActivationButtonDwellMs).IsEqualTo(value);
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.ActionMenuDwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("200");
        Check.That(ex.Message).Contains("2000");
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
        Check.That(settings.ActionMenuDwellMs).IsEqualTo(value);
    }

    #endregion

    #region DwellSettings - ManualSelectionActionMenuDwellMs Validation

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void ManualSelectionActionMenuDwellMs_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.ManualSelectionActionMenuDwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
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
        Check.ThatCode(() =>
        {
            settings.ToolbarButtonDwellMs = value;
        }).Throws<ArgumentOutOfRangeException>();
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.KeyboardKeyDwellMs = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("100");
        Check.That(ex.Message).Contains("1500");
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
        Check.ThatCode(() =>
        {
            settings.KeyboardPredictionDwellMs = value;
        }).Throws<ArgumentOutOfRangeException>();
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.FixationRadiusPixels = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("10");
        Check.That(ex.Message).Contains("200");
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
        Check.That(settings.FixationRadiusPixels).IsEqualTo(value);
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.VelocityThresholdPxPerSec = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("must be greater than 0");
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
        Check.That(settings.VelocityThresholdPxPerSec).IsEqualTo(value);
    }

    #endregion

    #region CursorSettings - SmoothingFactor Validation

    [Fact]
    public void SmoothingFactor_BelowZero_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.SmoothingFactor = -0.1; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("0");
        Check.That(ex.Message).Contains("1");
        Check.That(ex.Message).Contains("SmoothingFactor");
    }

    [Fact]
    public void SmoothingFactor_AboveOne_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.SmoothingFactor = 1.1; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("0");
        Check.That(ex.Message).Contains("1");
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
        Check.That(settings.SmoothingFactor).IsEqualTo(value);
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.OneEuroMinCutoff = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("0.1");
        Check.That(ex.Message).Contains("20");
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
        Check.That(settings.OneEuroMinCutoff).IsEqualTo(value);
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.OneEuroBeta = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("0");
        Check.That(ex.Message).Contains("5");
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.SnapRadiusPixels = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("0");
        Check.That(ex.Message).Contains("100");
    }

    #endregion

    #region CursorSettings - CircleColor Validation

    [Fact]
    public void CircleColor_InvalidHex_ThrowsArgumentException()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        ArgumentException? ex = null;
        try { settings.CircleColor = "invalid"; }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("value");
        Check.That(ex.Message).Contains("CircleColor");
        Check.That(ex.Message).Contains("hex color");
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
        Check.ThatCode(() =>
        {
            settings.CircleColor = value;
        }).Throws<ArgumentException>();
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
        Check.That(settings.CircleColor).IsEqualTo(value);
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.CircleOpacity = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("0");
        Check.That(ex.Message).Contains("1");
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.CircleThickness = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("1");
        Check.That(ex.Message).Contains("10");
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
        ArgumentOutOfRangeException? ex = null;
        try { settings.CircleSize = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("10");
        Check.That(ex.Message).Contains("500");
    }

    #endregion

    #region ManualSelectionSettings - CircleRadiusPixels Validation

    [Theory]
    [InlineData(49)]
    [InlineData(501)]
    public void ManualSelectionSettings_CircleRadiusPixels_OutOfRange_ThrowsArgumentOutOfRange(int value)
    {
        // Arrange
        var settings = new ManualSelectionSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.CircleRadiusPixels = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("50");
        Check.That(ex.Message).Contains("500");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    [InlineData(500)]
    public void ManualSelectionSettings_CircleRadiusPixels_ValidValue_AllowsChange(int value)
    {
        // Arrange
        var settings = new ManualSelectionSettings();

        // Act
        settings.CircleRadiusPixels = value;

        // Assert
        Check.That(settings.CircleRadiusPixels).IsEqualTo(value);
    }

    #endregion

    #region ManualSelectionSettings - MagnificationLevel Validation

    [Theory]
    [InlineData(1.4)]
    [InlineData(5.1)]
    public void ManualSelectionSettings_MagnificationLevel_OutOfRange_ThrowsArgumentOutOfRange(double value)
    {
        // Arrange
        var settings = new ManualSelectionSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.MagnificationLevel = value; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("1.5");
        Check.That(ex.Message).Contains("5");
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void ManualSelectionSettings_MagnificationLevel_ValidValue_AllowsChange(double value)
    {
        // Arrange
        var settings = new ManualSelectionSettings();

        // Act
        settings.MagnificationLevel = value;

        // Assert
        Check.That(settings.MagnificationLevel).IsEqualTo(value);
    }

    #endregion

    #region General Validation Patterns

    [Fact]
    public void OnChanging_InvalidValue_DoesNotChangeProperty()
    {
        // Arrange
        var settings = new DwellSettings { FixationDetectionMs = 300 };

        // Act & Assert - attempt to set invalid value
        Check.ThatCode(() =>
        {
            settings.FixationDetectionMs = 50;
        }).Throws<ArgumentOutOfRangeException>();

        // Property should retain original value
        Check.That(settings.FixationDetectionMs).IsEqualTo(300);
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
        Check.That(propertyChangedFired).IsTrue();
        Check.That(changedPropertyName).IsEqualTo(nameof(settings.FixationDetectionMs));
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
        Check.That(settings.FixationDetectionMs).IsEqualTo(400);
        Check.That(settings.ActivationButtonDwellMs).IsEqualTo(800);
        Check.That(settings.ActionMenuDwellMs).IsEqualTo(700);
        Check.That(settings.FixationRadiusPixels).IsEqualTo(75.0);
    }

    [Fact]
    public void MultipleProperties_MixedValidAndInvalid_OnlyInvalidThrows()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act - set valid value
        settings.FixationDetectionMs = 400;
        Check.That(settings.FixationDetectionMs).IsEqualTo(400);

        // Act & Assert - attempt invalid value
        Check.ThatCode(() =>
        {
            settings.ActivationButtonDwellMs = 50;
        }).Throws<ArgumentOutOfRangeException>();

        // First property should remain changed
        Check.That(settings.FixationDetectionMs).IsEqualTo(400);
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
        Check.That(fireCount).IsEqualTo(0);

        // Act - set to different value
        settings.FixationDetectionMs = 400;

        // Assert - PropertyChanged should fire once
        Check.That(fireCount).IsEqualTo(1);
    }

    #endregion

    #region Exception Message Quality

    [Fact]
    public void ValidationException_ContainsPropertyName()
    {
        // Arrange
        var settings = new CursorSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.SmoothingFactor = -1; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("SmoothingFactor");
    }

    [Fact]
    public void ValidationException_ContainsValidRange()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.FixationDetectionMs = 50; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("100");
        Check.That(ex.Message).Contains("1000");
    }

    [Fact]
    public void ValidationException_ContainsActualValue()
    {
        // Arrange
        var settings = new DwellSettings();

        // Act & Assert
        ArgumentOutOfRangeException? ex = null;
        try { settings.FixationDetectionMs = 5000; }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex.Message).Contains("5000");
    }

    #endregion

    #region Thread Safety and Performance

    [Fact(Skip = "Performance test with hard timing threshold - can be flaky in CI")]
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

        Check.That(medianTime < 100).IsTrue();
    }

    #endregion
}
