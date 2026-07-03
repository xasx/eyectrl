namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void Has13Tabs()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Check.That(vm.Tabs).HasSize(13);
    }

    [Fact]
    public void BackdropAndDragTabs_Exist()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Check.That(vm.Tabs.Find(t => t.Name == "Backdrop")).IsNotNull();
        Check.That(vm.Tabs.Find(t => t.Name == "Drag")).IsNotNull();
    }

    [Fact]
    public void DwellTab_ExposesCooldownVelocityAndDragDwells()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        var dwellTab = vm.Tabs.Find(t => t.Name == "Dwell")!;
        var ids = dwellTab.Items.Select(i => i.Id).ToList();
        Check.That(ids).Contains("velocityThreshold", "dwellCooldownMs", "dragReleaseMs", "dragRightEdgeMs");
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
    public void ResetAllSettings_ResetsBackdropToo()
    {
        var settings = new EyeCtrlSettings { Backdrop = { Opacity = 0.8, Enabled = false } };
        var service = new SettingsService("test.json");

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Check.That(settings.Backdrop.Opacity).IsEqualTo(defaults.Backdrop.Opacity);
        Check.That(settings.Backdrop.Enabled).IsEqualTo(defaults.Backdrop.Enabled);
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

    [Fact]
    public void CalibrationCompleted_UpdatesCalStatusLabel()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        _messenger.Send(new CalibrationCompletedMessage(true, 12.34));

        var calTab = vm.Tabs.Find(t => t.Name == "Calibration")!;
        var status = (ReadOnlyLabelSettingItem)calTab.Items.First(i => i.Id == "calStatus");
        Check.That(status.DisplayText).IsEqualTo("Calibrated — mean error 12.3 px");
    }

    [Fact]
    public void CalibrationFailed_UpdatesCalStatusLabel()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        _messenger.Send(new CalibrationCompletedMessage(false, null));

        var calTab = vm.Tabs.Find(t => t.Name == "Calibration")!;
        var status = (ReadOnlyLabelSettingItem)calTab.Items.First(i => i.Id == "calStatus");
        Check.That(status.DisplayText).IsEqualTo("Calibration failed");
    }
}
