namespace EyeCtrl.App.Views.Settings;

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
                new ComboBoxSpec("s_mode", "Selection Mode",
                    new[] { "None", "Manual Selection", "Auto Selection" },
                    new[] { "None", "ManualSelection", "AutoSelection" },
                    () => s.SelectionMode.ToString(),
                    v => { s.SelectionMode = Enum.Parse<SelectionMode>(v); _ctx.ApplyAndSave(); })
                    { Description = "None clicks your gaze point; Manual zooms; Auto snaps." },
                new SliderSpec("s_rad", "Circle Radius (px)", 50, 300, 10, 0,
                    () => s.ManualSelection.CircleRadiusPixels, v => { s.ManualSelection.CircleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Radius of the magnifier's zoom circle." },
                new SliderSpec("s_mag", "Magnification Level", 1.5, 5, 0.5, 1,
                    () => s.ManualSelection.MagnificationLevel, v => { s.ManualSelection.MagnificationLevel = v; _ctx.ApplyAndSave(); })
                    { Description = "How strongly the zoom circle magnifies." },
                new SliderSpec("s_grace", "Grace Period (ms)", 0, 2000, 50, 0,
                    () => s.ManualSelection.ManualSelectionGracePeriodMs, v => { s.ManualSelection.ManualSelectionGracePeriodMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell waits this long after manual selection opens." },
                // The strictness knob for the anchored commit rule: raise toward the circle's
                // logical radius for "always clicks, sometimes off-target", lower for "clicks where
                // you aimed, sometimes refuses".
                new SliderSpec("s_hold", "Hold Tolerance (px)", 20, 300, 5, 0,
                    () => s.ManualSelection.HoldRadiusLogical, v => { s.ManualSelection.HoldRadiusLogical = v; _ctx.ApplyAndSave(); })
                    { Description = "How far your gaze may drift from the held point before the selection restarts. Larger is more forgiving." },
                new ColorPickerSpec("s_highlight", "Highlight Color",
                    () => s.AutoSelection.HighlightColor, v => { s.AutoSelection.HighlightColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ControlBtnRadius)
                    { Description = "Color of the box around an auto-selected control." },
                new SliderSpec("s_highlightThickness", "Highlight Thickness", 1, 10, 1, 0,
                    () => s.AutoSelection.HighlightThickness, v => { s.AutoSelection.HighlightThickness = v; _ctx.ApplyAndSave(); })
                    { Description = "Line weight of that auto-selection box." },
                // Stepped by 16 to match ControlFinder's ring spacing: values in between buy no
                // extra ring, and each ring that IS added costs up to 8 more UIA probes.
                new SliderSpec("s_snapRadius", "Snap Search Radius (px)", 16, 96, 16, 0,
                    () => s.AutoSelection.SnapSearchRadiusPixels, v => { s.AutoSelection.SnapSearchRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How far from your gaze Auto Selection searches for a control." },
                new SliderSpec("s_gi_outer", "Gaze Ring Diameter (px)", 20, 200, 5, 0,
                    () => s.ManualSelection.GazeIndicatorOuterDiameter, v => { s.ManualSelection.GazeIndicatorOuterDiameter = v; _ctx.ApplyAndSave(); })
                    { Description = "Diameter of the ring shown at your gaze; applies from the next selection." },
                new SliderSpec("s_gi_dot", "Center Dot (px)", 2, 40, 1, 0,
                    () => s.ManualSelection.GazeIndicatorCenterDotDiameter, v => { s.ManualSelection.GazeIndicatorCenterDotDiameter = v; _ctx.ApplyAndSave(); })
                    { Description = "Diameter of the dot at the center of the gaze ring; applies after a restart." },
                new SliderSpec("s_gi_stroke", "Ring Stroke (px)", 0.5, 10, 0.5, 1,
                    () => s.ManualSelection.GazeIndicatorStrokeThickness, v => { s.ManualSelection.GazeIndicatorStrokeThickness = v; _ctx.ApplyAndSave(); })
                    { Description = "Thickness of the gaze ring's outline; applies after a restart." },
                new SliderSpec("s_gi_opacity", "Ring Opacity", 0.1, 1.0, 0.05, 2,
                    () => s.ManualSelection.GazeIndicatorOpacity, v => { s.ManualSelection.GazeIndicatorOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How visible the gaze ring is; applies after a restart." },
                new SliderSpec("s_gi_anim", "Reset Animation (ms)", 0, 1000, 50, 0,
                    () => s.ManualSelection.GazeIndicatorResetAnimationMs, v => { s.ManualSelection.GazeIndicatorResetAnimationMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long the gaze ring takes to fade after a selection; applies after a restart." },
            ]),
        ];
    }
}
