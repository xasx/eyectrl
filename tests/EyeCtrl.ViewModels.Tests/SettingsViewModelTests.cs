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

        // Most tabs fit on one page, but verify navigation works. TotalPages is owned by
        // the SettingsWindow shell (set from the real page list), so simulate it as
        // RebuildContent would before navigating.
        vm.ShowTab("Cursor");
        vm.TotalPages = 2;
        Check.That(vm.CurrentPage).IsEqualTo(0);
        Check.That(vm.CurrentPageItems).Not.IsEmpty();

        vm.NextPage();
        Check.That(vm.CurrentPage).IsEqualTo(1);

        vm.PreviousPage();
        Check.That(vm.CurrentPage).IsEqualTo(0);
    }

    [Fact]
    public void NextPage_DoesNotRecomputeTotalPages_FromTheLegacyItemSystem()
    {
        // The SettingsWindow shell owns TotalPages — it sets it from the real page list
        // (RebuildContent: TotalPages = _pages.Count). The legacy UpdatePageItems recompute
        // used to clobber it on every page advance; when the legacy count exceeded the real
        // one, CurrentPage could advance past the last real page and RenderCurrentPage
        // rendered nothing — empty pages with the bar still showing.
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ShowTab("Cursor");

        vm.TotalPages = 4;   // simulate the shell's value (4 height-chunked pages)
        vm.CurrentPage = 0;

        vm.NextPage();

        Check.That(vm.CurrentPage).IsEqualTo(1);
        Check.That(vm.TotalPages).IsEqualTo(4);
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
    public void ResetAllSettings_CoversEveryEnumeratedSubSetting()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleSize = 400;
        settings.Dwell.FixationDetectionMs = 999;
        settings.Backdrop.Opacity = 0.8;
        settings.Drag.SettleDwellMs = 1500;
        settings.Scroll.Sensitivity = 1.9;

        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        var defaultSubs = defaults.EnumerateSubSettings().ToDictionary(t => t.Name, t => t.Settings);

        foreach (var (name, sub) in settings.EnumerateSubSettings())
        {
            var defaultSub = defaultSubs[name];
            var comparableProps = sub.GetType().GetProperties()
                .Where(p => p.CanRead && p.CanWrite &&
                            (p.PropertyType.IsValueType || p.PropertyType == typeof(string)));
            foreach (var prop in comparableProps)
            {
                Check.WithCustomMessage($"{name}.{prop.Name} was not reset")
                    .That(prop.GetValue(sub)).IsEqualTo(prop.GetValue(defaultSub));
            }
        }
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
