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
        original.Zoom.Enabled = true;
        original.Zoom.MagnificationLevel = 3.0;
        original.Keyboard.ActiveLayout = "AZERTY";
        original.Toolbar.Size = 120;
        original.Theme.Mode = "Dark";
        original.General.StartPaused = true;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(99, deserialized.Cursor.CircleSize);
        Assert.Equal("#FF0000", deserialized.Cursor.CircleColor);
        Assert.Equal(500, deserialized.Dwell.FixationDetectionMs);
        Assert.True(deserialized.Zoom.Enabled);
        Assert.Equal(3.0, deserialized.Zoom.MagnificationLevel);
        Assert.Equal("AZERTY", deserialized.Keyboard.ActiveLayout);
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
    public void ZoomSettings_Position_RoundTrips()
    {
        var settings = new ZoomSettings { Position = ViewPosition.AtCenter };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ZoomSettings>(json, JsonOptions)!;
        Assert.Equal(ViewPosition.AtCenter, deserialized.Position);
    }

    [Fact]
    public void ActionMenuSettings_Position_RoundTrips()
    {
        var settings = new ActionMenuSettings { Position = ViewPosition.AtCenter };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ActionMenuSettings>(json, JsonOptions)!;
        Assert.Equal(ViewPosition.AtCenter, deserialized.Position);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPositions_RoundTripTogether()
    {
        var original = new EyeCtrlSettings();
        original.Zoom.Position = ViewPosition.AtCenter;
        original.ActionMenu.Position = ViewPosition.AtCenter;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(ViewPosition.AtCenter, deserialized.Zoom.Position);
        Assert.Equal(ViewPosition.AtCenter, deserialized.ActionMenu.Position);
    }

    [Fact]
    public void Settings_BackwardsCompatible_MissingViewPosition_UsesDefault()
    {
        // Old settings JSON without Position property
        var json = """
        {
          "zoom": {
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

        // Should default to AtGaze when not specified
        Assert.Equal(ViewPosition.AtGaze, settings.Zoom.Position);
        Assert.Equal(ViewPosition.AtGaze, settings.ActionMenu.Position);
    }
}
