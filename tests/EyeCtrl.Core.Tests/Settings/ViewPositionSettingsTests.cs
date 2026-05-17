namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class ViewPositionSettingsTests
{
    [Fact]
    public void ManualSelectionSettings_DefaultPosition_IsAtGaze()
    {
        var settings = new ManualSelectionSettings();

        Assert.Equal(ViewPosition.AtGaze, settings.Position);
    }

    [Fact]
    public void ActionMenuSettings_DefaultPosition_IsAtGaze()
    {
        var settings = new ActionMenuSettings();

        Assert.Equal(ViewPosition.AtGaze, settings.Position);
    }

    [Fact]
    public void ManualSelectionSettings_CanSetPosition_ToAtCenter()
    {
        var settings = new ManualSelectionSettings
        {
            Position = ViewPosition.AtCenter
        };

        Assert.Equal(ViewPosition.AtCenter, settings.Position);
    }

    [Fact]
    public void ActionMenuSettings_CanSetPosition_ToAtCenter()
    {
        var settings = new ActionMenuSettings
        {
            Position = ViewPosition.AtCenter
        };

        Assert.Equal(ViewPosition.AtCenter, settings.Position);
    }

    [Fact]
    public void ManualSelectionSettings_Position_RaisesPropertyChanged()
    {
        var settings = new ManualSelectionSettings();
        bool propertyChanged = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManualSelectionSettings.Position))
                propertyChanged = true;
        };

        settings.Position = ViewPosition.AtCenter;

        Assert.True(propertyChanged);
    }

    [Fact]
    public void ActionMenuSettings_Position_RaisesPropertyChanged()
    {
        var settings = new ActionMenuSettings();
        bool propertyChanged = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionMenuSettings.Position))
                propertyChanged = true;
        };

        settings.Position = ViewPosition.AtCenter;

        Assert.True(propertyChanged);
    }
}
