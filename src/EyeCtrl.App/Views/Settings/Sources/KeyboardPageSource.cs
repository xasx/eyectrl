using EyeCtrl.App.Services;
using EyeCtrl.Core.Localization;

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
                new ComboBoxSpec("k_layout",
                    StringTable.Current.T("settings.keyboard.layout.label"),
                    [
                        StringTable.Current.T("settings.keyboard.layout.option.qwerty"),
                        StringTable.Current.T("settings.keyboard.layout.option.qwertz"),
                        StringTable.Current.T("settings.keyboard.layout.option.azerty"),
                    ],
                    new[] { "QWERTY", "QWERTZ", "AZERTY" },
                    () => k.ActiveLayout, v => { k.ActiveLayout = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.layout.description") },
                new SliderSpec("k_size",
                    StringTable.Current.T("settings.keyboard.size.label"), 0.5, 2.0, 0.1, 1,
                    () => k.KeySizeMultiplier, v => { k.KeySizeMultiplier = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.size.description") },
                new ToggleSpec("k_pred",
                    StringTable.Current.T("settings.keyboard.prediction.label"),
                    () => k.PredictionEnabled, v => { k.PredictionEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.prediction.description") },
                new SliderSpec("k_pred_count",
                    StringTable.Current.T("settings.keyboard.prediction_count.label"), 1, Core.Settings.KeyboardSettings.MaxSuggestionsCeiling, 1, 0,
                    () => k.MaxSuggestions, v => { k.MaxSuggestions = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.prediction_count.description") },
                new ComboBoxSpec("k_lang",
                    StringTable.Current.T("settings.keyboard.dictionary_language.label"),
                    [
                        StringTable.Current.T("settings.keyboard.dictionary_language.option.english"),
                        StringTable.Current.T("settings.keyboard.dictionary_language.option.french"),
                        StringTable.Current.T("settings.keyboard.dictionary_language.option.spanish"),
                        StringTable.Current.T("settings.keyboard.dictionary_language.option.german"),
                        StringTable.Current.T("settings.keyboard.dictionary_language.option.polish"),
                    ],
                    new[] { "en", "fr", "es", "de", "pl" },
                    () => string.IsNullOrEmpty(k.LanguagePack) ? "en" : k.LanguagePack,
                    v => { k.LanguagePack = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.dictionary_language.description") },
                new ToggleSpec("k_sound",
                    StringTable.Current.T("settings.keyboard.click_sound.label"),
                    () => k.ClickSoundEnabled, v => { k.ClickSoundEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.click_sound.description") },
                new ComboBoxSpec("k_edge",
                    StringTable.Current.T("settings.keyboard.screen_edge.label"),
                    [
                        StringTable.Current.T("settings.keyboard.screen_edge.option.bottom"),
                        StringTable.Current.T("settings.keyboard.screen_edge.option.top"),
                    ],
                    new[] { "Bottom", "Top" },
                    () => k.ScreenEdge, v => { k.ScreenEdge = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.keyboard.screen_edge.description") },
                new ButtonSpec("k_reset",
                    StringTable.Current.T("settings.keyboard.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.keyboard.reset.message"), () =>
                    {
                        var def = new Core.Settings.KeyboardSettings();
                        k.ActiveLayout = def.ActiveLayout;
                        k.KeySizeMultiplier = def.KeySizeMultiplier;
                        k.PredictionEnabled = def.PredictionEnabled;
                        k.MaxSuggestions = def.MaxSuggestions;
                        k.LanguagePack = def.LanguagePack;
                        k.ClickSoundEnabled = def.ClickSoundEnabled;
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
                new LabelSpec(() => StringTable.Current.T("settings.keyboard.dictionary.empty")),
            ]);
            yield break;
        }

        var rows = new List<SettingsRowSpec>();
        foreach (var word in learned)
        {
            var captured = word;
            rows.Add(new LabelSpec(
                () => StringTable.Current.T("settings.keyboard.dictionary.word", captured)));
            rows.Add(new ButtonSpec("kb_wd_del_" + captured,
                StringTable.Current.T("settings.keyboard.dictionary.remove.label"),
                () => _ctx.ShowModal(
                    StringTable.Current.T("settings.keyboard.dictionary.remove.message", captured), () =>
                {
                    // Only a genuine write failure is worth a gaze user's error-dwell: the
                    // live view legitimately lists never-persisted Add Word learns, whose file
                    // rewrite has nothing to remove.
                    if (dictionary.ForgetWord(captured) == UserDictionaryService.DictionaryWriteResult.WriteFailed)
                        _ctx.ShowError(StringTable.Current.T(
                            "settings.keyboard.dictionary.remove.partial_error", captured));
                    _ctx.RebuildContent();
                }),
                ButtonKind.Delete, Width: 420, Height: 70));
        }

        rows.Add(new ButtonSpec("kb_wd_clear",
            StringTable.Current.T("settings.keyboard.dictionary.clear.label"),
            () => _ctx.ShowModal(
                StringTable.Current.T("settings.keyboard.dictionary.clear.message", learned.Count), () =>
            {
                if (dictionary.ClearLearned() == UserDictionaryService.DictionaryWriteResult.WriteFailed)
                    _ctx.ShowError(StringTable.Current.T("settings.keyboard.dictionary.clear.partial_error"));
                _ctx.RebuildContent();
            }),
            ButtonKind.Reset, Width: 420, Height: 100));

        yield return new PageSpec(rows);
    }
}
