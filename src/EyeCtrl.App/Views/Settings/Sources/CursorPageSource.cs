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
                c.RingStyle == CursorRingStyle.AchromaticReticle
                    ? (SettingsRowSpec)new LabelSpec(
                        () => "Circle Color is not used by the Achromatic reticle ring style.")
                    : new ColorPickerSpec("c_color", "Circle Color",
                        () => c.CircleColor,
                        v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                        SettingsWindow.ColorSwatchRadius)
                        { Description = "The cursor ring’s colour; every preset stays clearly visible." },
            ]),
            new PageSpec([
                new ComboBoxSpec("c_ring_style", "Ring Style",
                    ["Weighted sandwich", "Bi-tonal segments", "Achromatic reticle"],
                    Enum.GetNames<CursorRingStyle>(),
                    () => c.RingStyle.ToString(),
                    v =>
                    {
                        c.RingStyle = Enum.Parse<CursorRingStyle>(v);

                        // Selects band structure only — deliberately writes no appearance value.
                        // Thickness, opacity, colour and size are all purely the user's; live
                        // testing showed that is what full control means, after an earlier
                        // revision already stopped writing thickness for the same reason.
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();   // still required: the Circle Color row swaps to
                                                 // a LabelSpec note under AchromaticReticle
                    })
                    { Description = "How the ring stays visible on light and dark backgrounds." },
                new SliderSpec("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => c.CircleOpacity,
                    v => { c.CircleOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "Lowering this dims the coloured half of the ring's edge, which is what keeps it visible." },
                new SliderSpec("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => c.CircleThickness,
                    v => { c.CircleThickness = v; _ctx.ApplyAndSave(); })
                    { Description = "Ring stroke width, in pixels." },
                new SliderSpec("c_gap", "Gap Interpolation (ms)", 0, 500, 25, 0,
                    () => c.MaxGapInterpolationMs, v => { c.MaxGapInterpolationMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Bridge short tracker dropouts up to this long." },
                new ComboBoxSpec("c_pos_style", "Position Indicator",
                    Enum.GetNames<Core.Models.PositionIndicatorStyle>(), null,
                    () => c.PositionIndicatorStyle.ToString(),
                    v => { c.PositionIndicatorStyle = Enum.Parse<Core.Models.PositionIndicatorStyle>(v); _ctx.ApplyAndSave(); })
                    { Description = "How the position indicator is drawn; applies from the next action-menu open." },
                new SliderSpec("c_size", "Circle Size (px)", 10, 500, 10, 0,
                    () => c.CircleSize, v => { c.CircleSize = v; _ctx.ApplyAndSave(); })
                    { Description = "Diameter of the cursor circle." },
                new SliderSpec("c_euro_cutoff", "One Euro Min Cutoff", 0.1, 20.0, 0.1, 1,
                    () => c.OneEuroMinCutoff, v => { c.OneEuroMinCutoff = v; _ctx.ApplyAndSave(); })
                    { Description = "Lower is smoother and slower; higher follows fast motion." },
                new SliderSpec("c_euro_beta", "One Euro Beta", 0.0, 5.0, 0.1, 1,
                    () => c.OneEuroBeta, v => { c.OneEuroBeta = v; _ctx.ApplyAndSave(); })
                    { Description = "How quickly speed is allowed to reduce smoothing." },
                new ButtonSpec("c_reset", "Reset Cursor Settings",
                    () => _ctx.ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var d = new CursorSettings();
                        c.FilterMode = d.FilterMode;
                        c.SmoothingFactor = d.SmoothingFactor;
                        c.RingStyle = d.RingStyle;
                        c.CircleColor = d.CircleColor;
                        c.CircleOpacity = d.CircleOpacity;
                        c.CircleThickness = d.CircleThickness;
                        c.MaxGapInterpolationMs = d.MaxGapInterpolationMs;
                        c.PositionIndicatorStyle = d.PositionIndicatorStyle;
                        c.CircleSize = d.CircleSize;
                        c.OneEuroMinCutoff = d.OneEuroMinCutoff;
                        c.OneEuroBeta = d.OneEuroBeta;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
