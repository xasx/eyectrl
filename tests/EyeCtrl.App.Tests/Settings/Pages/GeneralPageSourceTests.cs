namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

public class GeneralPageSourceTests
{
    [Fact]
    public void General_OnePageWithThemeBootPauseTrackAndThreeButtons()
    {
        var rows = new GeneralPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Single().Id).IsEqualTo("g_theme");
        Check.That(rows.OfType<ToggleSpec>().Select(t => t.Id))
            .Contains(new[] { "g_boot", "g_pause", "g_track" });
        var buttons = rows.OfType<ButtonSpec>().ToList();
        Check.That(buttons.Select(b => b.Id)).Contains(new[] { "g_restart", "g_reset", "g_resetall" });
        // The General page has THREE reset buttons (g_restart, g_reset, g_resetall), so
        // ContainsExactly(ButtonKind.Reset) would wrongly pin count=1 and fail. NFluent 3.1.0 has
        // no ContainsOnly on IEnumerable<T> (CS1061 — the AGENTS.md cheat sheet lists
        // ContainsOnlyElementsThatMatch, not ContainsOnly). This asserts "all (any count) buttons
        // are Reset kind" without pinning the count.
        Check.That(buttons.Select(b => b.Kind)).ContainsOnlyElementsThatMatch(k => k == ButtonKind.Reset);
    }

    [Fact]
    public void General_WhenTheSupervisorIsAvailable_ShowsTheCrashRecoveryToggleAndItsLiveStatus()
    {
        var settings = new EyeCtrlSettings();
        var ctx = TestSettingsPageContext.Build(
            settings: settings, supervision: BuildSupervision(settings, available: true));
        var rows = new GeneralPageSource(ctx).Build().Single().Rows;

        var toggle = rows.OfType<ToggleSpec>().Single(t => t.Id == "g_supervision");
        Check.That(toggle.Label).IsEqualTo("Automatic Crash Recovery");
        Check.That(toggle.Description).IsNotNull();
        Check.That(toggle.Description!).Contains("restarts automatically");
        Check.That(toggle.Get()).IsFalse();

        // The status line is the deferred LabelSpec next to the toggle; on a fresh install it
        // says Off — nothing restarts EyeCtrl after a crash.
        Check.That(rows.OfType<LabelSpec>().Any(l => l.Text().StartsWith("Off"))).IsTrue();
    }

    [Fact]
    public void General_WhenTheSupervisorIsNotAvailable_ShowsTheReasonInsteadOfAToggle()
    {
        var settings = new EyeCtrlSettings();
        var ctx = TestSettingsPageContext.Build(
            settings: settings, supervision: BuildSupervision(settings, available: false));
        var rows = new GeneralPageSource(ctx).Build().Single().Rows;

        // Same swap convention as the Cursor page's unavailable rows: no dead dwell target,
        // the reason in its place.
        Check.That(rows.OfType<ToggleSpec>().Any(t => t.Id == "g_supervision")).IsFalse();
        Check.That(rows.OfType<LabelSpec>().Any(l => l.Text().Contains("not installed"))).IsTrue();
    }

    [Fact]
    public void General_TheCrashRecoveryToggle_DrivesTheServiceInBothDirections()
    {
        // R4 pin: the row's setter must route On to RequestEnable and Off to RequestDisable.
        // A composition-backed service makes a wrong or dead direction observable (the enabled
        // flag would stay false), instead of the silent no-op an inverted setter produced.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_t04_row_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            var (_, toggle) = SupervisionComposition.Create(
                settings, path, new FakeStartupRegistrar(),
                enablePublishing: () => { },
                launchSupervisor: () => true,
                tryReachSupervisor: () => true,
                isSupervisorInstalled: () => true,
                isLaunchRegistered: () => true,
                runInBackground: work => work(),
                postToUi: work => work());

            var ctx = TestSettingsPageContext.Build(settings: settings, supervision: toggle);
            var row = new GeneralPageSource(ctx).Build().Single().Rows
                .OfType<ToggleSpec>().Single(t => t.Id == "g_supervision");

            row.Set(true);
            Check.That(settings.Supervision.Enabled).IsTrue();
            Check.That(toggle.Enabled).IsTrue();

            row.Set(false);
            Check.That(settings.Supervision.Enabled).IsFalse();
            Check.That(toggle.Enabled).IsFalse();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) { }
    }

    private static SupervisionToggleService BuildSupervision(EyeCtrlSettings settings, bool available) =>
        new(settings,
            new SupervisorBringUp(
                settings,
                enablePublishing: () => { },
                launchSupervisor: () => false,
                persistEnabled: _ => { },
                reconcileRunKey: () => { },
                tryReachSupervisor: () => false),
            persistEnabled: _ => { },
            supervisorInstalled: () => available,
            reconcileRunKey: () => { },
            runInBackground: work => work(),
            postToUi: work => work());
}
