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
                // The span is DELIBERATELY left at the guard's full 0.1–20 Hz even though the top of it
                // does nothing on this hardware: OneEuroFilter caps the cutoff at sampleRate/3, which is
                // ~10 Hz at the PCEye 5's ~33 Hz, so 10–20 behaves identically to 10. The cap is a
                // function of the DEVICE rate, not a constant — a 60 Hz provider caps at 20 and uses the
                // whole span — so narrowing the slider would hard-code one tracker's rate into the UI,
                // and narrowing the Core guard instead would invalidate persisted values and drag the
                // migration's clamps with it. Documented here and in the row description rather than
                // silently trimmed; a range change is a separate proposal.
                new SliderSpec("c_euro_cutoff", "One Euro Min Cutoff", 0.1, 20.0, 0.1, 1,
                    () => c.OneEuroMinCutoff, v => { c.OneEuroMinCutoff = v; _ctx.ApplyAndSave(); })
                    { Description = "Lower is smoother and slower; higher follows fast motion. Above about a third of the tracker's sample rate (~10 Hz here) there is no further effect." },
                // Range/step rescaled with the units (CursorSettings.OneEuroBetaPerDiagonal). The old
                // spec was 0–5 by 0.1 in Hz-per-pixel-per-second, where the correct value was ~0.01:
                // the smallest non-zero position the user could reach was ten times too large, so the
                // only usable setting on the whole slider was exactly 0. 0–60 by 0.5 puts the default
                // (20) in the middle with room either side.
                new SliderSpec("c_euro_beta", "One Euro Beta", 0.0, 60.0, 0.5, 1,
                    () => c.OneEuroBetaPerDiagonal, v => { c.OneEuroBetaPerDiagonal = v; _ctx.ApplyAndSave(); })
                    { Description = "Extra responsiveness per screen diagonal per second of gaze movement." },
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
                        c.OneEuroBetaPerDiagonal = d.OneEuroBetaPerDiagonal;
                        // No slider of their own (the stabiliser that reads them is not built yet),
                        // but Reset still owns them — otherwise "reset cursor settings" would leave
                        // two cursor settings behind.
                        c.DeadbandRadiusPx = d.DeadbandRadiusPx;
                        c.DeadbandConfirmSamples = d.DeadbandConfirmSamples;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
