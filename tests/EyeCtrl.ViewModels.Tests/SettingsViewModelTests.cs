namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Settings;

public class SettingsViewModelTests
{
    [Fact]
    public void ShowTab_SetsTabAndResetsPage()
    {
        var vm = new SettingsViewModel(new EyeCtrlSettings());

        vm.TotalPages = 3;
        vm.CurrentPage = 2;
        vm.ShowTab("Cursor");

        Check.That(vm.CurrentTabName).IsEqualTo("Cursor");
        Check.That(vm.CurrentPage).IsEqualTo(0);
    }

    [Fact]
    public void Pagination_Works()
    {
        var vm = new SettingsViewModel(new EyeCtrlSettings());

        vm.ShowTab("Cursor");
        vm.TotalPages = 2;
        Check.That(vm.CurrentPage).IsEqualTo(0);

        vm.NextPage();
        Check.That(vm.CurrentPage).IsEqualTo(1);

        vm.PreviousPage();
        Check.That(vm.CurrentPage).IsEqualTo(0);
    }

    [Fact]
    public void NextPage_DoesNotRecomputeTotalPages()
    {
        // The SettingsWindow shell owns TotalPages — it sets it from the real page list
        // (RebuildContent: TotalPages = _pages.Count). The VM must never clobber it.
        var vm = new SettingsViewModel(new EyeCtrlSettings());
        vm.ShowTab("Cursor");

        vm.TotalPages = 4;
        vm.NextPage();

        Check.That(vm.CurrentPage).IsEqualTo(1);
        Check.That(vm.TotalPages).IsEqualTo(4);
    }

    [Fact]
    public void ResetAll_RestoresDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleSize = 400; // valid non-default (max is 500)
        settings.Dwell.FixationDetectionMs = 999;

        var vm = new SettingsViewModel(settings);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Check.That(settings.Cursor.CircleSize).IsEqualTo(defaults.Cursor.CircleSize);
        Check.That(settings.Dwell.FixationDetectionMs).IsEqualTo(defaults.Dwell.FixationDetectionMs);
    }

    [Fact]
    public void ResetAllSettings_ResetsEyeCareToo()
    {
        var settings = new EyeCtrlSettings { EyeCare = { Enabled = true, IntervalMinutes = 99 } };

        new SettingsViewModel(settings).ResetAllSettings();

        Check.That(settings.EyeCare.Enabled).IsFalse();
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(20);
    }

    [Fact]
    public void ResetAllSettings_ResetsBackdropToo()
    {
        var settings = new EyeCtrlSettings { Backdrop = { Opacity = 0.8, Enabled = false } };

        new SettingsViewModel(settings).ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Check.That(settings.Backdrop.Opacity).IsEqualTo(defaults.Backdrop.Opacity);
        Check.That(settings.Backdrop.Enabled).IsEqualTo(defaults.Backdrop.Enabled);
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

        new SettingsViewModel(settings).ResetAllSettings();

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
}
