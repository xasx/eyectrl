namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels.Messages;

internal sealed class AdaptationPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    private readonly Action? _onOpenAdaptationLog;

    public AdaptationPageSource(SettingsPageContext ctx, Action? onOpenAdaptationLog = null)
    {
        _ctx = ctx;
        _onOpenAdaptationLog = onOpenAdaptationLog;
    }

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings.Adaptation;
        return
        [
            new PageSpec([
                new ToggleSpec("ad_enabled",
                    StringTable.Current.T("settings.adaptation.enabled.label"),
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.enabled.description") },
                new ComboBoxSpec("ad_posture",
                    StringTable.Current.T("settings.adaptation.posture.label"),
                    [
                        StringTable.Current.T("settings.adaptation.posture.option.conservative"),
                        StringTable.Current.T("settings.adaptation.posture.option.balanced"),
                        StringTable.Current.T("settings.adaptation.posture.option.responsive"),
                    ],
                    Enum.GetNames<Core.Settings.AdaptationPosture>(),
                    () => s.Posture.ToString(),
                    v => { s.Posture = Enum.Parse<Core.Settings.AdaptationPosture>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.posture.description") },
                new ToggleSpec("ad_dwell",
                    StringTable.Current.T("settings.adaptation.dwell.label"),
                    () => s.AdaptDwell, v => { s.AdaptDwell = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.dwell.description") },
                new ToggleSpec("ad_tolerance",
                    StringTable.Current.T("settings.adaptation.tolerance.label"),
                    () => s.AdaptTolerance, v => { s.AdaptTolerance = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.tolerance.description") },
                new ToggleSpec("ad_smoothing",
                    StringTable.Current.T("settings.adaptation.smoothing.label"),
                    () => s.AdaptSmoothing, v => { s.AdaptSmoothing = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.smoothing.description") },
                new ToggleSpec("ad_geometry",
                    StringTable.Current.T("settings.adaptation.geometry.label"),
                    () => s.AdaptGeometry, v => { s.AdaptGeometry = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.geometry.description") },
                new ToggleSpec("ad_cue",
                    StringTable.Current.T("settings.adaptation.cue.label"),
                    () => s.ShowCue, v => { s.ShowCue = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.cue.description") },
                new ToggleSpec("ad_sensitive",
                    StringTable.Current.T("settings.adaptation.sensitive.label"),
                    () => s.SuppressInSensitiveContexts, v => { s.SuppressInSensitiveContexts = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.sensitive.description") },
                new ToggleSpec("ad_caregiver",
                    StringTable.Current.T("settings.adaptation.caregiver.label"),
                    () => s.CaregiverAlerts, v => { s.CaregiverAlerts = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.caregiver.description") },
                new SliderSpec("ad_caregiver_interval",
                    StringTable.Current.T("settings.adaptation.caregiver_interval.label"), 1.0, 60.0, 1.0, 0,
                    () => s.CaregiverMinIntervalMinutes, v => { s.CaregiverMinIntervalMinutes = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.caregiver_interval.description") },
                new SliderSpec("ad_frac_dwell_short",
                    StringTable.Current.T("settings.adaptation.dwell_shorter.label"), 0.0, 1.0, 0.05, 2,
                    () => s.DwellShorterMaxFraction, v => { s.DwellShorterMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.dwell_shorter.description") },
                new SliderSpec("ad_frac_dwell_long",
                    StringTable.Current.T("settings.adaptation.dwell_longer.label"), 0.0, 1.0, 0.05, 2,
                    () => s.DwellLongerMaxFraction, v => { s.DwellLongerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.dwell_longer.description") },
                new SliderSpec("ad_frac_tol_loose",
                    StringTable.Current.T("settings.adaptation.tolerance_looser.label"), 0.0, 1.0, 0.05, 2,
                    () => s.ToleranceLooserMaxFraction, v => { s.ToleranceLooserMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.tolerance_looser.description") },
                new SliderSpec("ad_frac_smooth",
                    StringTable.Current.T("settings.adaptation.smoothing_more.label"), 0.0, 1.0, 0.05, 2,
                    () => s.SmoothingMoreMaxFraction, v => { s.SmoothingMoreMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.smoothing_more.description") },
                new SliderSpec("ad_frac_geom",
                    StringTable.Current.T("settings.adaptation.geometry_bigger.label"), 0.0, 1.0, 0.05, 2,
                    () => s.GeometryBiggerMaxFraction, v => { s.GeometryBiggerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.geometry_bigger.description") },
                new SliderSpec("ad_geom_margin",
                    StringTable.Current.T("settings.adaptation.growth_cap.label"), 0.0, 300.0, 5.0, 0,
                    () => s.GeometryMaxMarginPx, v => { s.GeometryMaxMarginPx = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.adaptation.growth_cap.description") },
                new ButtonSpec("ad_reset",
                    StringTable.Current.T("settings.adaptation.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.adaptation.reset.message"), () =>
                    {
                        var d = new Core.Settings.AdaptationSettings();
                        s.Enabled = d.Enabled;
                        s.Posture = d.Posture;
                        s.AdaptDwell = d.AdaptDwell;
                        s.AdaptTolerance = d.AdaptTolerance;
                        s.AdaptSmoothing = d.AdaptSmoothing;
                        s.AdaptGeometry = d.AdaptGeometry;
                        s.ShowCue = d.ShowCue;
                        s.SuppressInSensitiveContexts = d.SuppressInSensitiveContexts;
                        s.CaregiverAlerts = d.CaregiverAlerts;
                        s.CaregiverMinIntervalMinutes = d.CaregiverMinIntervalMinutes;
                        s.DwellShorterMaxFraction = d.DwellShorterMaxFraction;
                        s.DwellLongerMaxFraction = d.DwellLongerMaxFraction;
                        s.ToleranceLooserMaxFraction = d.ToleranceLooserMaxFraction;
                        s.SmoothingMoreMaxFraction = d.SmoothingMoreMaxFraction;
                        s.GeometryBiggerMaxFraction = d.GeometryBiggerMaxFraction;
                        s.GeometryMaxMarginPx = d.GeometryMaxMarginPx;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("ad_reset_learned",
                    StringTable.Current.T("settings.adaptation.reset_learned.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.adaptation.reset_learned.message"), () =>
                        _ctx.Messenger.Send(new ResetLearnedOffsetsMessage())),
                    ButtonKind.Reset, Width: 300, Height: 100)
                    { Description = StringTable.Current.T("settings.adaptation.reset_learned.description") },
                new ButtonSpec("ad_log",
                    StringTable.Current.T("settings.adaptation.log.label"),
                    () => _onOpenAdaptationLog?.Invoke(),
                    ButtonKind.Default, Width: 300, Height: 100)
                    { Description = StringTable.Current.T("settings.adaptation.log.description") },
            ]),
        ];
    }
}
