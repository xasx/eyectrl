namespace EyeCtrl.App.Views.Settings;

internal sealed class AdvancedPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public AdvancedPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

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
                new ToggleSpec("av_backdrop_enabled", "Enable Backdrop",
                    () => backdrop.Enabled, v => { backdrop.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Dim what's behind the keyboard and other overlays." },
                new ComboBoxSpec("av_backdrop_type", "Backdrop Type",
                    ["Acrylic", "Mica", "Solid Darken"], Enum.GetNames<Core.BackdropType>(),
                    () => backdrop.Type.ToString(),
                    v => { backdrop.Type = Enum.Parse<Core.BackdropType>(v); _ctx.ApplyAndSave(); })
                    { Description = "The dimming material; Acrylic and Mica blur, Solid Darken only tints." },
                new SliderSpec("av_backdrop_opacity", "Backdrop Opacity", 0.1, 0.9, 0.05, 2,
                    () => backdrop.Opacity, v => { backdrop.Opacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How dark the backdrop is — only Solid Darken uses this; applies from the next open." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // Drag
                new SliderSpec("av_drag_settle", "Drag Settle Dwell (ms)", 200, 2000, 50, 0,
                    () => drag.SettleDwellMs, v => { drag.SettleDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long gaze must hold still before a drag starts. Applies after a restart." },
                new SliderSpec("av_drag_radius", "Drag Settle Radius (px)", 10, 200, 5, 0,
                    () => drag.SettleRadiusPixels, v => { drag.SettleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How far gaze may drift while settling into a drag. Applies after a restart." },
                new ColorPickerSpec("av_drag_color", "Drag Trail Color",
                    () => drag.TrailColor, v => { drag.TrailColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = "Colour of the trail drawn while dragging." },
                new SliderSpec("av_drag_opacity", "Drag Trail Opacity", 0.1, 1.0, 0.05, 2,
                    () => drag.TrailOpacity, v => { drag.TrailOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How visible the drag trail is." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // Scroll
                new SliderSpec("av_scroll_sens", "Scroll Sensitivity", 0.5, 2.0, 0.1, 1,
                    () => scroll.Sensitivity, v => { scroll.Sensitivity = v; _ctx.ApplyAndSave(); })
                    { Description = "How fast gaze scrolling moves through documents." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // External Dwell
                new ToggleSpec("av_ext_enabled", "Enable External Dwell",
                    () => ext.Enabled, v => { ext.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Let other apps trigger dwell through EyeCtrl's pipe." },
                new SliderSpec("av_ext_dwell", "External Dwell (ms)", 200, 3000, 50, 0,
                    () => ext.DefaultDwellMs, v => { ext.DefaultDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell length applied when an external app registers a target." },
                new SliderSpec("av_ext_cooldown", "External Cooldown (ms)", 0, 5000, 100, 0,
                    () => ext.CooldownMs, v => { ext.CooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Minimum wait between external dwell activations." },
                new ButtonSpec("av_reset", "Reset Advanced Settings",
                    () => _ctx.ShowModal("Reset Advanced settings to defaults?", () =>
                    {
                        var b = new Core.Settings.BackdropSettings();
                        var d = new Core.Settings.DragSettings();
                        var sc = new Core.Settings.ScrollSettings();
                        var e = new Core.Settings.ExternalDwellSettings();
                        st.Backdrop.Enabled = b.Enabled;
                        st.Backdrop.Type = b.Type;
                        st.Backdrop.Opacity = b.Opacity;
                        st.Drag.SettleDwellMs = d.SettleDwellMs;
                        st.Drag.SettleRadiusPixels = d.SettleRadiusPixels;
                        st.Drag.TrailColor = d.TrailColor;
                        st.Drag.TrailOpacity = d.TrailOpacity;
                        st.Scroll.Sensitivity = sc.Sensitivity;
                        st.ExternalDwell.Enabled = e.Enabled;
                        st.ExternalDwell.DefaultDwellMs = e.DefaultDwellMs;
                        st.ExternalDwell.CooldownMs = e.CooldownMs;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
