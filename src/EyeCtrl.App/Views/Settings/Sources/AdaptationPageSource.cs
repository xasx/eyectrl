namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
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
                new ToggleSpec("ad_enabled", "Enable Adaptation",
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Ease dwell and targeting automatically while you struggle." },
                new ComboBoxSpec("ad_posture", "Posture",
                    Enum.GetNames<Core.Settings.AdaptationPosture>(), null,
                    () => s.Posture.ToString(),
                    v => { s.Posture = Enum.Parse<Core.Settings.AdaptationPosture>(v); _ctx.ApplyAndSave(); })
                    { Description = "How readily adaptation responds to struggle." },
                new ToggleSpec("ad_dwell", "Adapt Dwell Time",
                    () => s.AdaptDwell, v => { s.AdaptDwell = v; _ctx.ApplyAndSave(); })
                    { Description = "Shorten or lengthen dwell times as you struggle or cruise." },
                new ToggleSpec("ad_tolerance", "Adapt Targeting Tolerance",
                    () => s.AdaptTolerance, v => { s.AdaptTolerance = v; _ctx.ApplyAndSave(); })
                    { Description = "Loosen or tighten how steadily you must rest on a target." },
                new ToggleSpec("ad_smoothing", "Adapt Smoothing",
                    () => s.AdaptSmoothing, v => { s.AdaptSmoothing = v; _ctx.ApplyAndSave(); })
                    { Description = "Adapt how strongly raw gaze is smoothed." },
                new ToggleSpec("ad_geometry", "Adapt Target Size",
                    () => s.AdaptGeometry, v => { s.AdaptGeometry = v; _ctx.ApplyAndSave(); })
                    { Description = "Grow or shrink targets when targeting is hard." },
                new ToggleSpec("ad_cue", "Show Adaptation Cue",
                    () => s.ShowCue, v => { s.ShowCue = v; _ctx.ApplyAndSave(); })
                    { Description = "Show a small cue while a setting is being adapted." },
                new ToggleSpec("ad_sensitive", "Suppress in Sensitive Contexts",
                    () => s.SuppressInSensitiveContexts, v => { s.SuppressInSensitiveContexts = v; _ctx.ApplyAndSave(); })
                    { Description = "Keep the cue and toasts hidden while you type passwords." },
                new SliderSpec("ad_frac_dwell_short", "Dwell — Shorter Limit", 0.0, 1.0, 0.05, 2,
                    () => s.DwellShorterMaxFraction, v => { s.DwellShorterMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a dwell may be shortened, as a share of its baseline." },
                new SliderSpec("ad_frac_dwell_long", "Dwell — Longer Limit", 0.0, 1.0, 0.05, 2,
                    () => s.DwellLongerMaxFraction, v => { s.DwellLongerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a dwell may be lengthened, as a share of its baseline." },
                new SliderSpec("ad_frac_tol_loose", "Tolerance — Looser Limit", 0.0, 1.0, 0.05, 2,
                    () => s.ToleranceLooserMaxFraction, v => { s.ToleranceLooserMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most tolerance may be loosened, as a share of its baseline." },
                new SliderSpec("ad_frac_smooth", "Smoothing — More Limit", 0.0, 1.0, 0.05, 2,
                    () => s.SmoothingMoreMaxFraction, v => { s.SmoothingMoreMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most smoothing may be increased, as a share of its baseline." },
                new SliderSpec("ad_frac_geom", "Target Size — Bigger Limit", 0.0, 1.0, 0.05, 2,
                    () => s.GeometryBiggerMaxFraction, v => { s.GeometryBiggerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a target may be grown, as a share of its baseline." },
                new SliderSpec("ad_geom_margin", "Target Size — Growth Cap", 0.0, 300.0, 5.0, 0,
                    () => s.GeometryMaxMarginPx, v => { s.GeometryMaxMarginPx = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a target's hit area may grow when adapted, in screen pixels." },
                new ButtonSpec("ad_reset", "Reset Adaptation Settings",
                    () => _ctx.ShowModal("Reset Adaptation settings to defaults?", () =>
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
                new ButtonSpec("ad_reset_learned", "Reset Learned Offsets",
                    () => _ctx.ShowModal("Reset the learned adaptation offsets to zero?", () =>
                        _ctx.Messenger.Send(new ResetLearnedOffsetsMessage())),
                    ButtonKind.Reset, Width: 300, Height: 100)
                    { Description = "Clear what the app has learned about your needs; it re-learns as you use it." },
                new ButtonSpec("ad_log", "Open Adaptation Log",
                    () => _onOpenAdaptationLog?.Invoke(),
                    ButtonKind.Default, Width: 300, Height: 100)
                    { Description = "Show the adaptation log file in Explorer." },
            ]),
        ];
    }
}
