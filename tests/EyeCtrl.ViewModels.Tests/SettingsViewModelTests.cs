namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Enums;
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

        // Exemptions, each a decision: the reset is asserted over every enumerated sub-setting
        // EXCEPT these. Supervision is Supervisor-owned (SettingsWriter.Supervisor); its enable
        // flag is turned off through the supervision row's sanctioned path, never a bulk reset.
        var notResetByDesign = new Dictionary<string, string>
        {
            ["Supervision"] =
                "Supervisor-owned section; turning crash recovery off must go through the "
                + "supervision row's sanctioned persist + Run-key path, not a bulk in-memory reset",
        };

        // Property-level exemptions inside a section the reset otherwise covers. D10 (the user's
        // call 2026-10-07): the language survives every reset, so General.UiLanguage is expected
        // to KEEP its perturbed-valued-shaped default and must not be equality-checked here.
        var notResetPropsByDesign = new HashSet<(string Sub, string Prop)>
        {
            ("General", nameof(GeneralSettings.UiLanguage)),
        };

        foreach (var (name, sub) in settings.EnumerateSubSettings())
        {
            if (notResetByDesign.ContainsKey(name))
                continue;

            var defaultSub = defaultSubs[name];
            var comparableProps = sub.GetType().GetProperties()
                .Where(p => p.CanRead && p.CanWrite &&
                            (p.PropertyType.IsValueType || p.PropertyType == typeof(string)));
            foreach (var prop in comparableProps)
            {
                if (notResetPropsByDesign.Contains((name, prop.Name)))
                    continue;

                Check.WithCustomMessage($"{name}.{prop.Name} was not reset")
                    .That(prop.GetValue(sub)).IsEqualTo(prop.GetValue(defaultSub));
            }
        }
    }

    [Fact]
    public void ResetAllSettings_KeepsTheLanguage()
    {
        // D10 (the user's call 2026-10-07). A user who chose English must not be dropped back
        // into German by the bulk reset — especially one who cannot read the reset button.
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = UiLanguage.English;
        settings.Cursor.CircleSize = 400;

        new SettingsViewModel(settings).ResetAllSettings();

        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.English);
        Check.That(settings.Cursor.CircleSize).IsEqualTo(new EyeCtrlSettings().Cursor.CircleSize);
    }

    [Fact]
    public void ResetAllSettings_LeavesTheSupervisorOwnedSectionUntouched()
    {
        // F4 pin: reset-all must not disarm the live crash policy while disk and the Supervisor
        // stay on. Supervision.Enabled and its siblings survive; other sections still reset.
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = true;
        settings.Supervision.MaxRetries = 7;
        settings.Cursor.CircleSize = 400;

        new SettingsViewModel(settings).ResetAllSettings();

        Check.That(settings.Supervision.Enabled).IsTrue();
        Check.That(settings.Supervision.MaxRetries).IsEqualTo(7);
        Check.That(settings.Cursor.CircleSize).IsEqualTo(new EyeCtrlSettings().Cursor.CircleSize);
    }
}
