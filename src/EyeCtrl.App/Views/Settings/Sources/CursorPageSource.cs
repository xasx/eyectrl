namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Views;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Cursor tab, the former <c>SettingsWindow.BuildCursorPages</c>: the filter/smoothing/
/// colour group, the ring-style combo, the appearance sliders (opacity, thickness, size), gap
/// interpolation, the position indicator and the two One-Euro sliders, with the reset button
/// last (copying all thirteen cursor settings). <see cref="PrefersAutoPagination"/> re-chunks
/// the rows per window height, so the page split is not pinned here. Snap radius lives on the
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
                new ComboBoxSpec("c_filter",
                    StringTable.Current.T("settings.cursor.filter.label"),
                    [
                        StringTable.Current.T("settings.cursor.filter.option.direct"),
                        StringTable.Current.T("settings.cursor.filter.option.smoothed"),
                        StringTable.Current.T("settings.cursor.filter.option.smoothed_legacy"),
                        StringTable.Current.T("settings.cursor.filter.option.one_euro"),
                    ],
                    Enum.GetNames<GazeFilterMode>(),
                    () => c.FilterMode.ToString(),
                    v => { c.FilterMode = Enum.Parse<GazeFilterMode>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.filter.description") },
                new SliderSpec("c_smooth",
                    StringTable.Current.T("settings.cursor.smoothing.label"), 0, 1, 0.1, 1,
                    () => c.SmoothingFactor,
                    v => { c.SmoothingFactor = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.smoothing.description") },
                c.RingStyle == CursorRingStyle.AchromaticReticle
                    ? (SettingsRowSpec)new LabelSpec(
                        () => StringTable.Current.T("settings.cursor.color_unavailable")) { Simple = true }
                    : new ColorPickerSpec("c_color",
                        StringTable.Current.T("settings.cursor.color.label"),
                        () => c.CircleColor,
                        v => { c.CircleColor = v; _ctx.ApplyAndSave(); },
                        SettingsWindow.ColorSwatchRadius)
                        { Description = StringTable.Current.T("settings.cursor.color.description"), Simple = true },
            ]),
            new PageSpec([
                new ComboBoxSpec("c_ring_style",
                    StringTable.Current.T("settings.cursor.ring_style.label"),
                    [
                        StringTable.Current.T("settings.cursor.ring_style.option.weighted_sandwich"),
                        StringTable.Current.T("settings.cursor.ring_style.option.bitonal_segments"),
                        StringTable.Current.T("settings.cursor.ring_style.option.achromatic_reticle"),
                    ],
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
                    { Description = StringTable.Current.T("settings.cursor.ring_style.description"), Simple = true },
                new SliderSpec("c_opacity",
                    StringTable.Current.T("settings.cursor.opacity.label"), 0.1, 1, 0.1, 1,
                    () => c.CircleOpacity,
                    v => { c.CircleOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.opacity.description"), Simple = true },
                new SliderSpec("c_thick",
                    StringTable.Current.T("settings.cursor.thickness.label"), 1, 6, 0.5, 1,
                    () => c.CircleThickness,
                    v => { c.CircleThickness = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.thickness.description") },
                new SliderSpec("c_gap",
                    StringTable.Current.T("settings.cursor.gap.label"), 0, 500, 25, 0,
                    () => c.MaxGapInterpolationMs, v => { c.MaxGapInterpolationMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.gap.description") },
                new ComboBoxSpec("c_pos_style",
                    StringTable.Current.T("settings.cursor.position_indicator.label"),
                    [
                        StringTable.Current.T("settings.cursor.position_indicator.option.crosshair"),
                        StringTable.Current.T("settings.cursor.position_indicator.option.pin"),
                    ],
                    Enum.GetNames<Core.Models.PositionIndicatorStyle>(),
                    () => c.PositionIndicatorStyle.ToString(),
                    v => { c.PositionIndicatorStyle = Enum.Parse<Core.Models.PositionIndicatorStyle>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.position_indicator.description") },
                new SliderSpec("c_size",
                    StringTable.Current.T("settings.cursor.size.label"), 10, 500, 10, 0,
                    () => c.CircleSize, v => { c.CircleSize = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.size.description"), Simple = true },
                // The span is DELIBERATELY left at the guard's full 0.1–20 Hz even though the top of it
                // does nothing on this hardware: OneEuroFilter caps the cutoff at sampleRate/3, which is
                // ~10 Hz at the PCEye 5's ~33 Hz, so 10–20 behaves identically to 10. The cap is a
                // function of the DEVICE rate, not a constant — a 60 Hz provider caps at 20 and uses the
                // whole span — so narrowing the slider would hard-code one tracker's rate into the UI,
                // and narrowing the Core guard instead would invalidate persisted values and drag the
                // migration's clamps with it. Documented here and in the row description rather than
                // silently trimmed; a range change is a separate proposal.
                new SliderSpec("c_euro_cutoff",
                    StringTable.Current.T("settings.cursor.one_euro_cutoff.label"), 0.1, 20.0, 0.1, 1,
                    () => c.OneEuroMinCutoff, v => { c.OneEuroMinCutoff = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.one_euro_cutoff.description") },
                // Range/step rescaled with the units (CursorSettings.OneEuroBetaPerDiagonal). The old
                // spec was 0–5 by 0.1 in Hz-per-pixel-per-second, where the correct value was ~0.01:
                // the smallest non-zero position the user could reach was ten times too large, so the
                // only usable setting on the whole slider was exactly 0. 0–60 by 0.5 puts the default
                // (20) in the middle with room either side.
                new SliderSpec("c_euro_beta",
                    StringTable.Current.T("settings.cursor.one_euro_beta.label"), 0.0, 60.0, 0.5, 1,
                    () => c.OneEuroBetaPerDiagonal, v => { c.OneEuroBetaPerDiagonal = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.cursor.one_euro_beta.description") },
                new ButtonSpec("c_reset",
                    StringTable.Current.T("settings.cursor.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.cursor.reset.message"), () =>
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
                        c.OneEuroBetaPerDiagonal = d.OneEuroBetaPerDiagonal;
                        // No slider of their own (the stabiliser that reads them is not built yet),
                        // but Reset still owns them — otherwise "reset cursor settings" would leave
                        // two cursor settings behind.
                        c.DeadbandRadiusPx = d.DeadbandRadiusPx;
                        c.DeadbandConfirmSamples = d.DeadbandConfirmSamples;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
            ]),
        ];
    }
}
