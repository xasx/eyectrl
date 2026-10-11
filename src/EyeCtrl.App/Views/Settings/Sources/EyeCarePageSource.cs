namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;

internal sealed class EyeCarePageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public EyeCarePageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings.EyeCare;
        return
        [
            new PageSpec([
                new ToggleSpec("ec_enabled",
                    StringTable.Current.T("settings.eyecare.enabled.label"),
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.enabled.description") },
                new SliderSpec("ec_interval",
                    StringTable.Current.T("settings.eyecare.break_interval.label"), 1, 120, 1, 0,
                    () => s.IntervalMinutes, v => { s.IntervalMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.break_interval.description") },
                new SliderSpec("ec_break",
                    StringTable.Current.T("settings.eyecare.natural_break.label"), 5, 600, 5, 0,
                    () => s.NaturalBreakSeconds, v => { s.NaturalBreakSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.natural_break.description") },
                new SliderSpec("ec_exercise",
                    StringTable.Current.T("settings.eyecare.exercise_duration.label"), 10, 180, 5, 0,
                    () => s.ExerciseDurationSeconds, v => { s.ExerciseDurationSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.exercise_duration.description") },
                new SliderSpec("ec_snooze_banner",
                    StringTable.Current.T("settings.eyecare.banner_snooze.label"), 5, 120, 5, 0,
                    () => s.BannerAutoSnoozeSeconds, v => { s.BannerAutoSnoozeSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.banner_snooze.description") },
                new SliderSpec("ec_snooze",
                    StringTable.Current.T("settings.eyecare.snooze.label"), 1, 60, 1, 0,
                    () => s.SnoozeMinutes, v => { s.SnoozeMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.snooze.description") },
                new ToggleSpec("ec_fatigue",
                    StringTable.Current.T("settings.eyecare.fatigue.label"),
                    () => s.FatigueMonitorEnabled, v => { s.FatigueMonitorEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.fatigue.description") },
                new SliderSpec("ec_fatigue_thresh",
                    StringTable.Current.T("settings.eyecare.fatigue_threshold.label"), 0.0, 1.0, 0.05, 2,
                    () => s.FatigueThreshold, v => { s.FatigueThreshold = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.fatigue_threshold.description") },
                new ButtonSpec("ec_reset",
                    StringTable.Current.T("settings.eyecare.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.eyecare.reset.message"), () =>
                    {
                        var d = new Core.Settings.EyeCareSettings();
                        s.Enabled = d.Enabled;
                        s.IntervalMinutes = d.IntervalMinutes;
                        s.NaturalBreakSeconds = d.NaturalBreakSeconds;
                        s.ExerciseDurationSeconds = d.ExerciseDurationSeconds;
                        s.BannerAutoSnoozeSeconds = d.BannerAutoSnoozeSeconds;
                        s.SnoozeMinutes = d.SnoozeMinutes;
                        s.FatigueMonitorEnabled = d.FatigueMonitorEnabled;
                        s.FatigueThreshold = d.FatigueThreshold;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
