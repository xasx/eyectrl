namespace EyeCtrl.App.Views.Settings;

internal sealed class KeyboardPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public KeyboardPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var k = _ctx.Settings.Keyboard;
        return
        [
            new PageSpec([
                new ComboBoxSpec("k_layout", "Active Layout",
                    new[] { "QWERTY", "QWERTZ", "AZERTY" }, null,
                    () => k.ActiveLayout, v => { k.ActiveLayout = v; _ctx.ApplyAndSave(); }),
                new SliderSpec("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
                    () => k.KeySizeMultiplier, v => { k.KeySizeMultiplier = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("k_pred", "Word Prediction",
                    () => k.PredictionEnabled, v => { k.PredictionEnabled = v; _ctx.ApplyAndSave(); }),
                new ComboBoxSpec("k_lang", "Dictionary Language",
                    new[] { "English", "French", "Spanish", "German", "Polish" },
                    new[] { "en", "fr", "es", "de", "pl" },
                    () => string.IsNullOrEmpty(k.LanguagePack) ? "en" : k.LanguagePack,
                    v => { k.LanguagePack = v; _ctx.ApplyAndSave(); }),
                new ToggleSpec("k_sound", "Click Sound",
                    () => k.ClickSoundEnabled, v => { k.ClickSoundEnabled = v; _ctx.ApplyAndSave(); }),
                new ComboBoxSpec("k_edge", "Screen Edge",
                    new[] { "Bottom", "Top" }, null,
                    () => k.ScreenEdge, v => { k.ScreenEdge = v; _ctx.ApplyAndSave(); }),
                new ButtonSpec("k_reset", "Reset Keyboard Settings",
                    () => _ctx.ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var def = new Core.Settings.KeyboardSettings();
                        k.ActiveLayout = def.ActiveLayout;
                        k.KeySizeMultiplier = def.KeySizeMultiplier;
                        k.PredictionEnabled = def.PredictionEnabled;
                        k.LanguagePack = "";
                        k.ScreenEdge = def.ScreenEdge;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
