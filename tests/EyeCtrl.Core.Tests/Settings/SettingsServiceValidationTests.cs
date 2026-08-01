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
        Check.That(File.Exists(_testFilePath)).IsTrue();
    }

    [Fact]
    public void Save_InvalidSettings_ShouldThrowException()
    {
        // Arrange: ActiveLayout setter accepts any string, but Validate() rejects unknown layouts
        var service = new SettingsService(_testFilePath);
        var settings = new EyeCtrlSettings();
        settings.Keyboard.ActiveLayout = "DVORAK";

        // Act & Assert
        Check.ThatCode(() => service.Save(settings)).Throws<InvalidOperationException>();
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
        var defaults = new EyeCtrlSettings();
        Check.That(settings.Cursor.SmoothingFactor).IsEqualTo(defaults.Cursor.SmoothingFactor);
        Check.That(settings.Cursor.CircleColor).IsEqualTo(defaults.Cursor.CircleColor);
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
        var defaults = new EyeCtrlSettings();
        Check.That(settings.Keyboard.ActiveLayout).IsEqualTo(defaults.Keyboard.ActiveLayout);
        Check.That(settings.Keyboard.ScreenEdge).IsEqualTo(defaults.Keyboard.ScreenEdge);
    }

    [Fact]
    public void Load_OutOfRangeValues_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Write settings with out-of-range values
        var invalidJson = @"{
            ""ManualSelection"": {
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
        var defaults = new EyeCtrlSettings();
        Check.That(settings.ManualSelection.CircleRadiusPixels).IsEqualTo(defaults.ManualSelection.CircleRadiusPixels);
        Check.That(settings.ManualSelection.MagnificationLevel).IsEqualTo(defaults.ManualSelection.MagnificationLevel);
        Check.That(settings.Toolbar.Size).IsEqualTo(defaults.Toolbar.Size);
    }

    [Fact]
    public void Load_NonExistentFile_ShouldReturnDefaults()
    {
        // Arrange
        var service = new SettingsService(_testFilePath);

        // Act
        var settings = service.Load();

        // Assert
        Check.That(settings).IsNotNull();
        Check.That(settings.Validate()).IsEmpty();
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
        Check.That(loadedSettings.Cursor.SmoothingFactor).IsEqualTo(0.7);
        Check.That(loadedSettings.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
        Check.That(loadedSettings.Validate()).IsEmpty();
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
                Name = "Contrast"
            }
        };

        // Act
        service.Save(settings);
        var loaded = service.Load();

        // Assert - all values should be preserved
        Check.That(loaded.Cursor.SmoothingFactor).IsEqualTo(0.8);
        Check.That(loaded.Cursor.CircleColor).IsEqualTo("#FF0000");
        Check.That(loaded.Cursor.CircleSize).IsEqualTo(300);
        Check.That(loaded.Dwell.FixationDetectionMs).IsEqualTo(500);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(1000);
        Check.That(loaded.Keyboard.ActiveLayout).IsEqualTo("QWERTZ");
        Check.That(loaded.Keyboard.ScreenEdge).IsEqualTo("Top");
        Check.That(loaded.Toolbar.DockedEdge).IsEqualTo("Left");
        Check.That(loaded.Toolbar.Size).IsEqualTo(100);
        Check.That(loaded.Theme.Name).IsEqualTo("Contrast");
    }

    [Fact]
    public void ThemeName_IsRejected_WhenNotAKnownTheme()
    {
        var settings = new EyeCtrlSettings();
        settings.Theme.Name = "Sepia";

        Check.That(settings.Validate())
             .HasElementThatMatches(e => e.Contains(nameof(ThemeSettings.Name)));
    }
}
