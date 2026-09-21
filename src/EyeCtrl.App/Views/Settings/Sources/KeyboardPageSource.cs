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
                    () => k.ActiveLayout, v => { k.ActiveLayout = v; _ctx.ApplyAndSave(); })
                    { Description = "Key arrangement; QWERTZ suits German, AZERTY suits French." },
                new SliderSpec("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
                    () => k.KeySizeMultiplier, v => { k.KeySizeMultiplier = v; _ctx.ApplyAndSave(); })
                    { Description = "Scales every key; bigger keys are easier to hit." },
                new ToggleSpec("k_pred", "Word Prediction",
                    () => k.PredictionEnabled, v => { k.PredictionEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Shows suggested words above the keyboard while you type." },
                new SliderSpec("k_pred_count", "Max Predictions", 1, 5, 1, 0,
                    () => k.MaxSuggestions, v => { k.MaxSuggestions = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How many suggestions fill the bar. Fewer are easier to aim; takes effect as you type." },
                new ComboBoxSpec("k_lang", "Dictionary Language",
                    new[] { "English", "French", "Spanish", "German", "Polish" },
                    new[] { "en", "fr", "es", "de", "pl" },
                    () => string.IsNullOrEmpty(k.LanguagePack) ? "en" : k.LanguagePack,
                    v => { k.LanguagePack = v; _ctx.ApplyAndSave(); })
                    { Description = "Which language the word suggestions draw from." },
                new ToggleSpec("k_sound", "Click Sound",
                    () => k.ClickSoundEnabled, v => { k.ClickSoundEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Plays a click each time a key activates." },
                new ComboBoxSpec("k_edge", "Screen Edge",
                    new[] { "Bottom", "Top" }, null,
                    () => k.ScreenEdge, v => { k.ScreenEdge = v; _ctx.ApplyAndSave(); })
                    { Description = "The screen edge the keyboard docks to." },
                new ButtonSpec("k_reset", "Reset Keyboard Settings",
                    () => _ctx.ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var def = new Core.Settings.KeyboardSettings();
                        k.ActiveLayout = def.ActiveLayout;
                        k.KeySizeMultiplier = def.KeySizeMultiplier;
                        k.PredictionEnabled = def.PredictionEnabled;
                        k.MaxSuggestions = def.MaxSuggestions;
                        k.LanguagePack = "";
                        k.ScreenEdge = def.ScreenEdge;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 380, Height: 100),
            ]),
            .. DictionaryPage(),
        ];
    }

    // ── Learned-words management (prediction controls #22) ──────────────────────────────────
    // The QA pattern: per-word Delete with a confirm modal and a rebuild, plus a Clear-all.
    // Built only when the host supplies the service — the headless test context does not, and
    // the Keyboard page then renders exactly as before.
    private IEnumerable<PageSpec> DictionaryPage()
    {
        var dictionary = _ctx.Dictionary;
        if (dictionary is null) yield break;

        var learned = dictionary.GetLearnedWords();
        if (learned.Count == 0)
        {
            yield return new PageSpec([
                new LabelSpec(() =>
                    "No learned words yet. They collect as you type; the Add Word key (Fn row) adds one on the spot."),
            ]);
            yield break;
        }

        var rows = new List<SettingsRowSpec>();
        foreach (var word in learned)
        {
            var captured = word;
            rows.Add(new LabelSpec(() => $"“{captured}”"));
            rows.Add(new ButtonSpec("kb_wd_del_" + captured,
                "✕  Remove from dictionary",
                () => _ctx.ShowModal($"Remove “{captured}” from the learned dictionary?", () =>
                {
                    dictionary.ForgetWord(captured);
                    _ctx.RebuildContent();
                }),
                ButtonKind.Delete, Width: 420, Height: 70));
        }

        rows.Add(new ButtonSpec("kb_wd_clear", "Remove ALL Learned Words",
            () => _ctx.ShowModal($"Remove all {learned.Count} learned words?", () =>
            {
                dictionary.ClearLearned();
                _ctx.RebuildContent();
            }),
            ButtonKind.Reset, Width: 420, Height: 100));

        yield return new PageSpec(rows);
    }
}
