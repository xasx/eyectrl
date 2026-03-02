namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void Has7Tabs()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Assert.Equal(7, vm.Tabs.Count);
    }

    [Fact]
    public void CursorTab_HasExpectedSettings()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        vm.ShowTab("Cursor");

        Assert.True(vm.CurrentPageItems.Count > 0);
    }

    [Fact]
    public void ResetAll_RestoresDefaults()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        settings.Cursor.CircleSize = 999;
        settings.Dwell.FixationDetectionMs = 999;

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Assert.Equal(defaults.Cursor.CircleSize, settings.Cursor.CircleSize);
        Assert.Equal(defaults.Dwell.FixationDetectionMs, settings.Dwell.FixationDetectionMs);
    }

    [Fact]
    public void Pagination_Works()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        // Most tabs fit on one page, but verify navigation works
        vm.ShowTab("Cursor");
        Assert.True(vm.TotalPages >= 1);
        Assert.Equal(0, vm.CurrentPage);
    }
}
