namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Views;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Cursor tab, page-for-page the former <c>SettingsWindow.BuildCursorPages</c>: filter mode,
/// smoothing, colour on page 1; opacity, thickness, reset on page 2. Snap radius lives on the
/// Selection tab with the rest of the AutoSelection knobs.
/// </summary>
internal sealed class CursorPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public CursorPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var c = _ctx.Settings.Cursor;
        return
        [
            new PageSpec([
                new ComboBoxSpec("c_filter", "Filter Mode", Enum.GetNames<GazeFilterMode>(), null,
                    () => c.FilterMode.ToString(),
                    v => { c.FilterMode = Enum.Parse<GazeFilterMode>(v); _ctx.ApplyAndSave(); })
                    { Description = "Filtering applied to raw gaze samples." },
                new SliderSpec("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
                    () => c.SmoothingFactor,
                    v => { c.SmoothingFactor = v; _ctx.ApplyAndSave(); })
                    { Description = "Higher is more responsive; lower is smoother." },
                new ColorPickerSpec("c_color", "Circle Color",
                    () => c.CircleColor,
                    v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = "The cursor ring’s colour; every preset stays clearly visible." },
            ]),
            new PageSpec([
                new SliderSpec("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => c.CircleOpacity,
                    v => { c.CircleOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How solid the ring is over what is behind it." },
                new SliderSpec("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => c.CircleThickness,
                    v => { c.CircleThickness = v; _ctx.ApplyAndSave(); })
                    { Description = "Ring stroke width, in pixels." },
                new ButtonSpec("c_reset", "Reset Cursor Settings",
                    () => _ctx.ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var d = new CursorSettings();
                        c.FilterMode = d.FilterMode;
                        c.SmoothingFactor = d.SmoothingFactor;
                        c.CircleColor = d.CircleColor;
                        c.CircleOpacity = d.CircleOpacity;
                        c.CircleThickness = d.CircleThickness;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
