namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class SelectionPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public SelectionPageSource(SettingsPageContext ctx) => _ctx = ctx;

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
                    v => { s.SelectionMode = Enum.Parse<SelectionMode>(v); _ctx.ApplyAndSave(); }),
                new SliderSpec("s_rad", "Circle Radius (px)", 50, 300, 10, 0,
                    () => s.ManualSelection.CircleRadiusPixels, v => { s.ManualSelection.CircleRadiusPixels = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("s_mag", "Magnification Level", 1.5, 5, 0.5, 1,
                    () => s.ManualSelection.MagnificationLevel, v => { s.ManualSelection.MagnificationLevel = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("s_grace", "Grace Period (ms)", 0, 2000, 50, 0,
                    () => s.ManualSelection.ManualSelectionGracePeriodMs, v => { s.ManualSelection.ManualSelectionGracePeriodMs = (int)v; _ctx.ApplyAndSave(); }),
                new ColorPickerSpec("s_highlight", "Highlight Color",
                    () => s.AutoSelection.HighlightColor, v => { s.AutoSelection.HighlightColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ControlBtnRadius),
                new SliderSpec("s_highlightThickness", "Highlight Thickness", 1, 10, 1, 0,
                    () => s.AutoSelection.HighlightThickness, v => { s.AutoSelection.HighlightThickness = v; _ctx.ApplyAndSave(); }),
            ]),
        ];
    }
}
