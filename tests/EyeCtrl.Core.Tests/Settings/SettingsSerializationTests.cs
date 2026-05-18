namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class SettingsSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Settings_RoundTrip_PreservesAllValues()
    {
        var original = new EyeCtrlSettings();
        original.Cursor.CircleSize = 99;
        original.Cursor.CircleColor = "#FF0000";
        original.Dwell.FixationDetectionMs = 500;
        original.ManualSelection.Enabled = true;
        original.ManualSelection.MagnificationLevel = 3.0;
        original.Keyboard.ActiveLayout = "AZERTY";
        original.Keyboard.SmartCapitalizationEnabled = false;
        original.Keyboard.NextWordPredictionEnabled = false;
        original.Keyboard.ShowPredictionConfidence = true;
        original.Keyboard.ShowPredictionTypes = true;
        original.Keyboard.EmojiSuggestionsEnabled = true;
        original.Toolbar.Size = 120;
        original.Theme.Mode = "Dark";
        original.General.StartPaused = true;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(99, deserialized.Cursor.CircleSize);
        Assert.Equal("#FF0000", deserialized.Cursor.CircleColor);
        Assert.Equal(500, deserialized.Dwell.FixationDetectionMs);
        Assert.True(deserialized.ManualSelection.Enabled);
        Assert.Equal(3.0, deserialized.ManualSelection.MagnificationLevel);
        Assert.Equal("AZERTY", deserialized.Keyboard.ActiveLayout);
        Assert.False(deserialized.Keyboard.SmartCapitalizationEnabled);
        Assert.False(deserialized.Keyboard.NextWordPredictionEnabled);
        Assert.True(deserialized.Keyboard.ShowPredictionConfidence);
        Assert.True(deserialized.Keyboard.ShowPredictionTypes);
        Assert.True(deserialized.Keyboard.EmojiSuggestionsEnabled);
        Assert.Equal(120, deserialized.Toolbar.Size);
        Assert.Equal("Dark", deserialized.Theme.Mode);
        Assert.True(deserialized.General.StartPaused);
    }

    [Fact]
    public void Settings_PropertyChanged_FiresOnChange()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        cursor.CircleSize = 100;
        cursor.CircleColor = "#FF0000";

        Assert.Contains("CircleSize", changed);
        Assert.Contains("CircleColor", changed);
    }

    [Fact]
    public void Settings_PropertyChanged_DoesNotFireForSameValue()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        var originalSize = cursor.CircleSize;
        cursor.CircleSize = originalSize;

        Assert.DoesNotContain("CircleSize", changed);
    }

    [Fact]
    public void Settings_BackwardsCompatible_DeserializesExistingJson()
    {
        var json = """
        {
          "cursor": {
            "filterMode": 3,
            "circleSize": 40,
            "circleColor": "#00FF00",
            "circleOpacity": 0.6,
            "circleThickness": 2.0,
            "circleHighContrast": false,
            "smoothingFactor": 0.5,
            "oneEuroMinCutoff": 5.0,
            "oneEuroBeta": 0.5,
            "snapRadiusPixels": 30
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;
        Assert.Equal(40, settings.Cursor.CircleSize);
        Assert.Equal("#00FF00", settings.Cursor.CircleColor);
    }

    [Fact]
    public void CursorSettings_PositionIndicatorStyle_DefaultIsCrosshair()
    {
        var settings = new CursorSettings();
        Assert.Equal(PositionIndicatorStyle.Crosshair, settings.PositionIndicatorStyle);
    }

    [Fact]
    public void CursorSettings_PositionIndicatorStyle_RoundTrips()
    {
        var settings = new CursorSettings { PositionIndicatorStyle = PositionIndicatorStyle.Pin };
        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<CursorSettings>(json)!;
        Assert.Equal(PositionIndicatorStyle.Pin, deserialized.PositionIndicatorStyle);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPosition_RoundTrips()
    {
        var settings = new EyeCtrlSettings { ViewPosition = ViewPosition.AtGaze };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;
        Assert.Equal(ViewPosition.AtGaze, deserialized.ViewPosition);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPositions_RoundTripTogether()
    {
        var original = new EyeCtrlSettings();
        original.ViewPosition = ViewPosition.AtGaze;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(ViewPosition.AtGaze, deserialized.ViewPosition);
    }

    [Fact]
    public void Settings_BackwardsCompatible_MissingViewPosition_UsesDefault()
    {
        // Old settings JSON without ViewPosition property
        var json = """
        {
          "ManualSelection": {
            "enabled": true,
            "circleRadiusPixels": 150,
            "magnificationLevel": 2.0
          },
          "actionMenu": {
            "visibleActions": [0, 1, 2]
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        // Should default to AtCenter (the new shared default)
        Assert.Equal(ViewPosition.AtCenter, settings.ViewPosition);
    }

    [Fact]
    public void KeyboardSettings_Phase4Properties_HaveCorrectDefaults()
    {
        var settings = new KeyboardSettings();

        // Phase 4 settings should have correct defaults
        Assert.True(settings.NextWordPredictionEnabled);
        Assert.False(settings.ShowPredictionConfidence);
        Assert.False(settings.ShowPredictionTypes);
        Assert.False(settings.EmojiSuggestionsEnabled);
    }

    [Fact]
    public void KeyboardSettings_Phase4Properties_RoundTrip()
    {
        var original = new KeyboardSettings
        {
            NextWordPredictionEnabled = false,
            ShowPredictionConfidence = true,
            ShowPredictionTypes = true,
            EmojiSuggestionsEnabled = true
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<KeyboardSettings>(json, JsonOptions)!;

        Assert.False(deserialized.NextWordPredictionEnabled);
        Assert.True(deserialized.ShowPredictionConfidence);
        Assert.True(deserialized.ShowPredictionTypes);
        Assert.True(deserialized.EmojiSuggestionsEnabled);
    }

    [Fact]
    public void Settings_BackwardsCompatible_MissingPhase4Properties_UsesDefaults()
    {
        // Old settings JSON without Phase 4 properties
        var json = """
        {
          "keyboard": {
            "activeLayout": "QWERTY",
            "predictionEnabled": true,
            "smartCapitalizationEnabled": true
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        // Should use defaults for missing Phase 4 properties
        Assert.True(settings.Keyboard.NextWordPredictionEnabled);
        Assert.False(settings.Keyboard.ShowPredictionConfidence);
        Assert.False(settings.Keyboard.ShowPredictionTypes);
        Assert.False(settings.Keyboard.EmojiSuggestionsEnabled);
    }

    [Fact]
    public void KeyboardSettings_Phase8Properties_HaveCorrectDefaults()
    {
        var settings = new KeyboardSettings();

        // Phase 8 settings should have correct defaults
        Assert.True(settings.DecayEnabled);
        Assert.Equal(30, settings.DecayHalfLifeDays);
    }

    [Fact]
    public void KeyboardSettings_Phase8Properties_RoundTrip()
    {
        var original = new KeyboardSettings
        {
            DecayEnabled = false,
            DecayHalfLifeDays = 60
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<KeyboardSettings>(json, JsonOptions)!;

        Assert.False(deserialized.DecayEnabled);
        Assert.Equal(60, deserialized.DecayHalfLifeDays);
    }

    [Fact]
    public void Settings_BackwardsCompatible_MissingPhase8Properties_UsesDefaults()
    {
        // Old settings JSON without Phase 8 properties
        var json = """
        {
          "keyboard": {
            "activeLayout": "QWERTY",
            "predictionEnabled": true,
            "smartCapitalizationEnabled": true,
            "nextWordPredictionEnabled": true
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        // Should use defaults for missing Phase 8 properties
        Assert.True(settings.Keyboard.DecayEnabled);
        Assert.Equal(30, settings.Keyboard.DecayHalfLifeDays);
    }
}
