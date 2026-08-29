namespace EyeCtrl.App.Views.Settings;

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
                new ToggleSpec("ec_enabled", "Enable Eye Care",
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Periodic break reminders and gaze exercises while you use EyeCtrl." },
                new SliderSpec("ec_interval", "Break Interval (min)", 1, 120, 1, 0,
                    () => s.IntervalMinutes, v => { s.IntervalMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How often a break is offered when you haven't rested." },
                new SliderSpec("ec_break", "Natural Break Length (s)", 5, 600, 5, 0,
                    () => s.NaturalBreakSeconds, v => { s.NaturalBreakSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Looking away this long counts as a natural break." },
                new SliderSpec("ec_exercise", "Exercise Duration (s)", 10, 180, 5, 0,
                    () => s.ExerciseDurationSeconds, v => { s.ExerciseDurationSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long each guided gaze exercise runs." },
                new SliderSpec("ec_snooze_banner", "Banner Auto-snooze (s)", 5, 120, 5, 0,
                    () => s.BannerAutoSnoozeSeconds, v => { s.BannerAutoSnoozeSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long an ignored banner waits before hiding itself. Applies after a restart." },
                new SliderSpec("ec_snooze", "Snooze Length (min)", 1, 60, 1, 0,
                    () => s.SnoozeMinutes, v => { s.SnoozeMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long breaks are postponed when you snooze a banner." },
                new ToggleSpec("ec_fatigue", "Fatigue Escalation",
                    () => s.FatigueMonitorEnabled, v => { s.FatigueMonitorEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Let rising fatigue bring breaks on early. Experimental." },
                new SliderSpec("ec_fatigue_thresh", "Fatigue Threshold", 0.0, 1.0, 0.05, 2,
                    () => s.FatigueThreshold, v => { s.FatigueThreshold = v; _ctx.ApplyAndSave(); })
                    { Description = "The fatigue score at which an early break fires." },
                new ButtonSpec("ec_reset", "Reset Eye Care Settings",
                    () => _ctx.ShowModal("Reset Eye Care settings to defaults?", () =>
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
