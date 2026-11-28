namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;

internal sealed class AdvancedPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public AdvancedPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    // Wave D D4: every dwell or look-time row moved to the „Verweilzeiten" tab (they are a
    // DwellPageSource row now). What remains here is the non-time tuning of the same surfaces.
    public IReadOnlyList<PageSpec> Build()
    {
        var st = _ctx.Settings;
        var backdrop = st.Backdrop;
        var drag = st.Drag;
        var scroll = st.Scroll;
        var ext = st.ExternalDwell;
        return
        [
            new PageSpec([
                // Backdrop
                new ToggleSpec("av_backdrop_enabled",
                    StringTable.Current.T("settings.advanced.backdrop_enabled.label"),
                    () => backdrop.Enabled, v => { backdrop.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_enabled.description") },
                new ComboBoxSpec("av_backdrop_type",
                    StringTable.Current.T("settings.advanced.backdrop_type.label"),
                    [
                        StringTable.Current.T("settings.advanced.backdrop_type.option.glass"),
                        StringTable.Current.T("settings.advanced.backdrop_type.option.solid_darken"),
                    ],
                    Enum.GetNames<Core.BackdropType>(),
                    () => backdrop.Type.ToString(),
                    v => { backdrop.Type = Enum.Parse<Core.BackdropType>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_type.description") },
                new SliderSpec("av_backdrop_opacity",
                    StringTable.Current.T("settings.advanced.backdrop_opacity.label"),
                    Core.Settings.BackdropSettings.OpacityMin, Core.Settings.BackdropSettings.OpacityMax,
                    0.05, 2,
                    () => backdrop.Opacity, v => { backdrop.Opacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_opacity.description") },
                new SeparatorSpec(),
                // Drag
                new SliderSpec("av_drag_radius",
                    StringTable.Current.T("settings.advanced.drag_radius.label"), 10, 200, 5, 0,
                    () => drag.SettleRadiusPixels, v => { drag.SettleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_radius.description") },
                new ColorPickerSpec("av_drag_color",
                    StringTable.Current.T("settings.advanced.drag_color.label"),
                    () => drag.TrailColor, v => { drag.TrailColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = StringTable.Current.T("settings.advanced.drag_color.description") },
                new SliderSpec("av_drag_opacity",
                    StringTable.Current.T("settings.advanced.drag_opacity.label"), 0.1, 1.0, 0.05, 2,
                    () => drag.TrailOpacity, v => { drag.TrailOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_opacity.description") },
                new SeparatorSpec(),
                // Scroll
                new SliderSpec("av_scroll_sens",
                    StringTable.Current.T("settings.advanced.scroll_sensitivity.label"), 0.5, 2.0, 0.1, 1,
                    () => scroll.Sensitivity, v => { scroll.Sensitivity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.scroll_sensitivity.description") },
                new SeparatorSpec(),
                // External Dwell
                new ToggleSpec("av_ext_enabled",
                    StringTable.Current.T("settings.advanced.external_dwell.label"),
                    () => ext.Enabled, v => { ext.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_dwell.description") },
                new SliderSpec("av_ext_cooldown",
                    StringTable.Current.T("settings.advanced.external_cooldown.label"), 0, 5000, 100, 0,
                    () => ext.CooldownMs, v => { ext.CooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_cooldown.description") },
                new ButtonSpec("av_reset",
                    StringTable.Current.T("settings.advanced.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.advanced.reset.message"), () =>
                    {
                        var b = new Core.Settings.BackdropSettings();
                        var d = new Core.Settings.DragSettings();
                        var sc = new Core.Settings.ScrollSettings();
                        var e = new Core.Settings.ExternalDwellSettings();
                        st.Backdrop.Enabled = b.Enabled;
                        st.Backdrop.Type = b.Type;
                        st.Backdrop.Opacity = b.Opacity;
                        st.Drag.SettleRadiusPixels = d.SettleRadiusPixels;
                        st.Drag.TrailColor = d.TrailColor;
                        st.Drag.TrailOpacity = d.TrailOpacity;
                        st.Scroll.Sensitivity = sc.Sensitivity;
                        st.ExternalDwell.Enabled = e.Enabled;
                        st.ExternalDwell.CooldownMs = e.CooldownMs;
                        // TrailColor's default is a Midnight absolute; re-derive for the live
                        // theme as startup does (review round 2, N2 — resets must not reopen
                        // the cross-theme ring/trail carry-over at runtime).
                        EyeCtrl.App.Services.ThemeStartup.MigrateHaloedMarks(
                            EyeCtrl.Controls.Theme.Tokens.Current, st);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
