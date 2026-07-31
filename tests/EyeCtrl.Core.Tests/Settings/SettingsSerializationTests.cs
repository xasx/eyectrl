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
        original.Theme.Name = "Ember";
        original.General.StartPaused = true;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Check.That(deserialized.Cursor.CircleSize).IsEqualTo(99);
        Check.That(deserialized.Cursor.CircleColor).IsEqualTo("#FF0000");
        Check.That(deserialized.Dwell.FixationDetectionMs).IsEqualTo(500);
        Check.That(deserialized.ManualSelection.Enabled).IsTrue();
        Check.That(deserialized.ManualSelection.MagnificationLevel).IsEqualTo(3.0);
        Check.That(deserialized.Keyboard.ActiveLayout).IsEqualTo("AZERTY");
        Check.That(deserialized.Keyboard.SmartCapitalizationEnabled).IsFalse();
        Check.That(deserialized.Keyboard.NextWordPredictionEnabled).IsFalse();
        Check.That(deserialized.Keyboard.ShowPredictionConfidence).IsTrue();
        Check.That(deserialized.Keyboard.ShowPredictionTypes).IsTrue();
        Check.That(deserialized.Keyboard.EmojiSuggestionsEnabled).IsTrue();
        Check.That(deserialized.Toolbar.Size).IsEqualTo(120);
        Check.That(deserialized.Theme.Name).IsEqualTo("Ember");
        Check.That(deserialized.General.StartPaused).IsTrue();
    }

    [Fact]
    public void Settings_PropertyChanged_FiresOnChange()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        cursor.CircleSize = 100;
        cursor.CircleColor = "#FF0000";

        Check.That(changed).Contains("CircleSize");
        Check.That(changed).Contains("CircleColor");
    }

    [Fact]
    public void Settings_PropertyChanged_DoesNotFireForSameValue()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        var originalSize = cursor.CircleSize;
        cursor.CircleSize = originalSize;

        Check.That(changed.Contains("CircleSize")).IsFalse();
    }

    [Fact]
    public void Settings_BackwardsCompatible_DeserializesExistingJson()
    {
        // circleHighContrast was removed when high contrast became a theme; it is
        // retained here deliberately, as an unknown member, to prove a pre-existing
        // settings.json still loads without migration code.
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
        Check.That(settings.Cursor.CircleSize).IsEqualTo(40);
        Check.That(settings.Cursor.CircleColor).IsEqualTo("#00FF00");
    }

    [Fact]
    public void CursorSettings_PositionIndicatorStyle_DefaultIsCrosshair()
    {
        var settings = new CursorSettings();
        Check.That(settings.PositionIndicatorStyle).IsEqualTo(PositionIndicatorStyle.Crosshair);
    }

    [Fact]
    public void CursorSettings_PositionIndicatorStyle_RoundTrips()
    {
        var settings = new CursorSettings { PositionIndicatorStyle = PositionIndicatorStyle.Pin };
        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<CursorSettings>(json)!;
        Check.That(deserialized.PositionIndicatorStyle).IsEqualTo(PositionIndicatorStyle.Pin);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPosition_RoundTrips()
    {
        var settings = new EyeCtrlSettings { ViewPosition = ViewPosition.AtGaze };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;
        Check.That(deserialized.ViewPosition).IsEqualTo(ViewPosition.AtGaze);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPositions_RoundTripTogether()
    {
        var original = new EyeCtrlSettings();
        original.ViewPosition = ViewPosition.AtGaze;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Check.That(deserialized.ViewPosition).IsEqualTo(ViewPosition.AtGaze);
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
        Check.That(settings.ViewPosition).IsEqualTo(ViewPosition.AtCenter);
    }

    [Fact]
    public void KeyboardSettings_Phase4Properties_HaveCorrectDefaults()
    {
        var settings = new KeyboardSettings();

        // Phase 4 settings should have correct defaults
        Check.That(settings.NextWordPredictionEnabled).IsTrue();
        Check.That(settings.ShowPredictionConfidence).IsFalse();
        Check.That(settings.ShowPredictionTypes).IsFalse();
        Check.That(settings.EmojiSuggestionsEnabled).IsFalse();
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

        Check.That(deserialized.NextWordPredictionEnabled).IsFalse();
        Check.That(deserialized.ShowPredictionConfidence).IsTrue();
        Check.That(deserialized.ShowPredictionTypes).IsTrue();
        Check.That(deserialized.EmojiSuggestionsEnabled).IsTrue();
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
        Check.That(settings.Keyboard.NextWordPredictionEnabled).IsTrue();
        Check.That(settings.Keyboard.ShowPredictionConfidence).IsFalse();
        Check.That(settings.Keyboard.ShowPredictionTypes).IsFalse();
        Check.That(settings.Keyboard.EmojiSuggestionsEnabled).IsFalse();
    }

    [Fact]
    public void KeyboardSettings_Phase8Properties_HaveCorrectDefaults()
    {
        var settings = new KeyboardSettings();

        // Phase 8 settings should have correct defaults
        Check.That(settings.DecayEnabled).IsTrue();
        Check.That(settings.DecayHalfLifeDays).IsEqualTo(30);
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

        Check.That(deserialized.DecayEnabled).IsFalse();
        Check.That(deserialized.DecayHalfLifeDays).IsEqualTo(60);
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
        Check.That(settings.Keyboard.DecayEnabled).IsTrue();
        Check.That(settings.Keyboard.DecayHalfLifeDays).IsEqualTo(30);
    }
}
