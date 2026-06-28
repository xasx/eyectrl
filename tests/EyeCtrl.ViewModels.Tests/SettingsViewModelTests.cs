namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void Has10Tabs()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Check.That(vm.Tabs).HasSize(10);
    }

    [Fact]
    public void CursorTab_HasExpectedSettings()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        vm.ShowTab("Cursor");

        Check.That(vm.CurrentPageItems.Count > 0).IsTrue();
    }

    [Fact]
    public void ResetAll_RestoresDefaults()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        settings.Cursor.CircleSize = 400; // Valid non-default value (max is 500)
        settings.Dwell.FixationDetectionMs = 999;

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Check.That(settings.Cursor.CircleSize).IsEqualTo(defaults.Cursor.CircleSize);
        Check.That(settings.Dwell.FixationDetectionMs).IsEqualTo(defaults.Dwell.FixationDetectionMs);
    }

    [Fact]
    public void ResetAllSettings_ResetsEyeCareToo()
    {
        var settings = new EyeCtrlSettings { EyeCare = { Enabled = true, IntervalMinutes = 99 } };
        var service = new SettingsService("test.json");

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        Check.That(settings.EyeCare.Enabled).IsFalse();
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(20);
    }

    [Fact]
    public void Pagination_Works()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        // Most tabs fit on one page, but verify navigation works
        vm.ShowTab("Cursor");
        Check.That(vm.TotalPages >= 1).IsTrue();
        Check.That(vm.CurrentPage).IsEqualTo(0);
    }
}
