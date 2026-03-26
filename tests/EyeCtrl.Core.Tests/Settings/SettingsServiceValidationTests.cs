namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;
using System.IO;
using Xunit;

public class SettingsServiceValidationTests : IDisposable
{
    private readonly string _testFilePath;

    public SettingsServiceValidationTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    [Fact]
    public void Save_ValidSettings_ShouldSucceed()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);
        var settings = new EyeCtrlSettings();

        // Act & Assert - should not throw
        service.Save(settings);
        Assert.True(File.Exists(_testFilePath));
    }

    [Fact]
    public void Save_InvalidSettings_ShouldThrowException()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);
        var settings = new EyeCtrlSettings
        {
            Cursor = new CursorSettings { SmoothingFactor = 2.0 }  // Invalid
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => service.Save(settings));
        Assert.Contains("Cannot save invalid settings", exception.Message);
        Assert.Contains("SmoothingFactor", exception.Message);
    }

    [Fact]
    public void Load_InvalidJsonSettings_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Write invalid settings JSON
        var invalidJson = @"{
            ""cursor"": {
                ""smoothingFactor"": 5.0,
                ""circleColor"": ""invalid""
            }
        }";
        File.WriteAllText(_testFilePath, invalidJson);

        // Act
        var settings = service.Load();

        // Assert - should return defaults instead of invalid settings
        Assert.Equal(0.5, settings.Cursor.SmoothingFactor);  // Default value
        Assert.Equal("#00FF00", settings.Cursor.CircleColor);  // Default value
    }

    [Fact]
    public void Load_CorruptedStringEnum_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Write settings with invalid string enum
        var invalidJson = @"{
            ""keyboard"": {
                ""activeLayout"": ""DVORAK"",
                ""screenEdge"": ""Center""
            }
        }";
        File.WriteAllText(_testFilePath, invalidJson);

        // Act
        var settings = service.Load();

        // Assert - should return defaults
        Assert.Equal("QWERTY", settings.Keyboard.ActiveLayout);
        Assert.Equal("Bottom", settings.Keyboard.ScreenEdge);
    }

    [Fact]
    public void Load_OutOfRangeValues_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Write settings with out-of-range values
        var invalidJson = @"{
            ""zoom"": {
                ""circleRadiusPixels"": 5000,
                ""magnificationLevel"": 10.0
            },
            ""toolbar"": {
                ""size"": 500
            }
        }";
        File.WriteAllText(_testFilePath, invalidJson);

        // Act
        var settings = service.Load();

        // Assert - should return defaults
        Assert.Equal(150, settings.Zoom.CircleRadiusPixels);  // Default
        Assert.Equal(2.0, settings.Zoom.MagnificationLevel);  // Default
        Assert.Equal(80, settings.Toolbar.Size);  // Default
    }

    [Fact]
    public void Load_NonExistentFile_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Act
        var settings = service.Load();

        // Assert
        Assert.NotNull(settings);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Load_ValidSettings_ShouldLoadCorrectly()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);
        var originalSettings = new EyeCtrlSettings
        {
            Cursor = new CursorSettings { SmoothingFactor = 0.7 },
            Keyboard = new KeyboardSettings { ActiveLayout = "AZERTY" }
        };
        service.Save(originalSettings);

        // Act
        var loadedSettings = service.Load();

        // Assert
        Assert.Equal(0.7, loadedSettings.Cursor.SmoothingFactor);
        Assert.Equal("AZERTY", loadedSettings.Keyboard.ActiveLayout);
        Assert.Empty(loadedSettings.Validate());
    }

    [Fact]
    public void RoundTrip_ValidSettings_ShouldPreserveValues()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);
        var settings = new EyeCtrlSettings
        {
            Cursor = new CursorSettings
            {
                SmoothingFactor = 0.8,
                CircleColor = "#FF0000",
                CircleSize = 300
            },
            Dwell = new DwellSettings
            {
                FixationDetectionMs = 500,
                ActivationButtonDwellMs = 1000
            },
            Keyboard = new KeyboardSettings
            {
                ActiveLayout = "QWERTZ",
                ScreenEdge = "Top"
            },
            Toolbar = new ToolbarSettings
            {
                DockedEdge = "Left",
                Size = 100
            },
            Theme = new ThemeSettings
            {
                Mode = "Dark",
                OverlayOpacity = 0.5
            }
        };

        // Act
        service.Save(settings);
        var loaded = service.Load();

        // Assert - all values should be preserved
        Assert.Equal(0.8, loaded.Cursor.SmoothingFactor);
        Assert.Equal("#FF0000", loaded.Cursor.CircleColor);
        Assert.Equal(300, loaded.Cursor.CircleSize);
        Assert.Equal(500, loaded.Dwell.FixationDetectionMs);
        Assert.Equal(1000, loaded.Dwell.ActivationButtonDwellMs);
        Assert.Equal("QWERTZ", loaded.Keyboard.ActiveLayout);
        Assert.Equal("Top", loaded.Keyboard.ScreenEdge);
        Assert.Equal("Left", loaded.Toolbar.DockedEdge);
        Assert.Equal(100, loaded.Toolbar.Size);
        Assert.Equal("Dark", loaded.Theme.Mode);
        Assert.Equal(0.5, loaded.Theme.OverlayOpacity);
    }
}
