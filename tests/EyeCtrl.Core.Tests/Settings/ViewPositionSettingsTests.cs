namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class ViewPositionSettingsTests
{
    [Fact]
    public void EyeCtrlSettings_DefaultViewPosition_IsAtCenter()
    {
        var settings = new EyeCtrlSettings();

        Check.That(settings.ViewPosition).IsEqualTo(ViewPosition.AtCenter);
    }

    [Fact]
    public void EyeCtrlSettings_CanSetViewPosition_ToAtGaze()
    {
        var settings = new EyeCtrlSettings
        {
            ViewPosition = ViewPosition.AtGaze
        };

        Check.That(settings.ViewPosition).IsEqualTo(ViewPosition.AtGaze);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPosition_RaisesPropertyChanged()
    {
        var settings = new EyeCtrlSettings();
        bool propertyChanged = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EyeCtrlSettings.ViewPosition))
                propertyChanged = true;
        };

        settings.ViewPosition = ViewPosition.AtGaze;

        Check.That(propertyChanged).IsTrue();
    }
}
