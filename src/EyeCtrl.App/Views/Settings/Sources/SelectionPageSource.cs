namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;

internal sealed class SelectionPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public SelectionPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("s_mode",
                    StringTable.Current.T("settings.selection.mode.label"),
                    [
                        StringTable.Current.T("settings.selection.mode.option.none"),
                        StringTable.Current.T("settings.selection.mode.option.manual"),
                        StringTable.Current.T("settings.selection.mode.option.auto"),
                    ],
                    new[] { "None", "ManualSelection", "AutoSelection" },
                    () => s.SelectionMode.ToString(),
                    v => { s.SelectionMode = Enum.Parse<SelectionMode>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.mode.description") },
                new SliderSpec("s_rad",
                    StringTable.Current.T("settings.selection.circle_radius.label"), 50, 300, 10, 0,
                    () => s.ManualSelection.CircleRadiusPixels, v => { s.ManualSelection.CircleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.circle_radius.description") },
                new SliderSpec("s_mag",
                    StringTable.Current.T("settings.selection.magnification.label"), 1.5, 5, 0.5, 1,
                    () => s.ManualSelection.MagnificationLevel, v => { s.ManualSelection.MagnificationLevel = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.magnification.description") },
                new SliderSpec("s_grace",
                    StringTable.Current.T("settings.selection.grace.label"), 0, 2000, 50, 0,
                    () => s.ManualSelection.ManualSelectionGracePeriodMs, v => { s.ManualSelection.ManualSelectionGracePeriodMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.grace.description") },
                // The strictness knob for the anchored commit rule: raise toward the circle's
                // logical radius for "always clicks, sometimes off-target", lower for "clicks where
                // you aimed, sometimes refuses".
                new SliderSpec("s_hold",
                    StringTable.Current.T("settings.selection.hold.label"), 20, 300, 5, 0,
                    () => s.ManualSelection.HoldRadiusLogical, v => { s.ManualSelection.HoldRadiusLogical = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.hold.description") },
                new ColorPickerSpec("s_highlight",
                    StringTable.Current.T("settings.selection.highlight.label"),
                    () => s.AutoSelection.HighlightColor, v => { s.AutoSelection.HighlightColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ControlBtnRadius)
                    { Description = StringTable.Current.T("settings.selection.highlight.description") },
                new SliderSpec("s_highlightThickness",
                    StringTable.Current.T("settings.selection.highlight_thickness.label"), 1, 10, 1, 0,
                    () => s.AutoSelection.HighlightThickness, v => { s.AutoSelection.HighlightThickness = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.highlight_thickness.description") },
                // Stepped by 16 to match ControlFinder's ring spacing: values in between buy no
                // extra ring, and each ring that IS added costs up to 8 more UIA probes.
                new SliderSpec("s_snapRadius",
                    StringTable.Current.T("settings.selection.snap_radius.label"), 16, 96, 16, 0,
                    () => s.AutoSelection.SnapSearchRadiusPixels, v => { s.AutoSelection.SnapSearchRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.snap_radius.description") },
                new SliderSpec("s_gi_outer",
                    StringTable.Current.T("settings.selection.gaze_ring.label"), 20, 200, 5, 0,
                    () => s.ManualSelection.GazeIndicatorOuterDiameter, v => { s.ManualSelection.GazeIndicatorOuterDiameter = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.gaze_ring.description") },
                new SliderSpec("s_gi_dot",
                    StringTable.Current.T("settings.selection.center_dot.label"), 2, 40, 1, 0,
                    () => s.ManualSelection.GazeIndicatorCenterDotDiameter, v => { s.ManualSelection.GazeIndicatorCenterDotDiameter = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.center_dot.description") },
                new SliderSpec("s_gi_stroke",
                    StringTable.Current.T("settings.selection.ring_stroke.label"), 0.5, 10, 0.5, 1,
                    () => s.ManualSelection.GazeIndicatorStrokeThickness, v => { s.ManualSelection.GazeIndicatorStrokeThickness = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.ring_stroke.description") },
                new SliderSpec("s_gi_opacity",
                    StringTable.Current.T("settings.selection.ring_opacity.label"), 0.1, 1.0, 0.05, 2,
                    () => s.ManualSelection.GazeIndicatorOpacity, v => { s.ManualSelection.GazeIndicatorOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.ring_opacity.description") },
                new SliderSpec("s_gi_anim",
                    StringTable.Current.T("settings.selection.reset_animation.label"), 0, 1000, 50, 0,
                    () => s.ManualSelection.GazeIndicatorResetAnimationMs, v => { s.ManualSelection.GazeIndicatorResetAnimationMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.selection.reset_animation.description") },
            ]),
        ];
    }
}
