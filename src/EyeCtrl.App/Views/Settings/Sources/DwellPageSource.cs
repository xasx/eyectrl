namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;

internal sealed class DwellPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public DwellPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var d = _ctx.Settings.Dwell;
        return
        [
            new PageSpec([
                new SliderSpec("d_fix",
                    StringTable.Current.T("settings.dwell.fixation.label"), 100, 500, 25, 0,
                    () => d.FixationDetectionMs, v => { d.FixationDetectionMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.fixation.description") },
                new SliderSpec("d_act",
                    StringTable.Current.T("settings.dwell.activation.label"), 200, 1500, 50, 0,
                    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.activation.description") },
                new SliderSpec("d_menu",
                    StringTable.Current.T("settings.dwell.action_menu.label"), 200, 1500, 50, 0,
                    () => d.ActionMenuDwellMs, v => { d.ActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.action_menu.description") },
                new SliderSpec("d_ManualSelection",
                    StringTable.Current.T("settings.dwell.manual_selection.label"), 200, 1500, 50, 0,
                    () => d.ManualSelectionActionMenuDwellMs, v => { d.ManualSelectionActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.manual_selection.description") },
            ]),
            new PageSpec([
                new SliderSpec("d_tool",
                    StringTable.Current.T("settings.dwell.toolbar.label"), 200, 1500, 50, 0,
                    () => d.ToolbarButtonDwellMs, v => { d.ToolbarButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.toolbar.description") },
                // 300 is the keyboard family's blink-credit floor, shared with the Core guard and the
                // adaptive clamp (Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs; the bound
                // and its derivation live beside KeyboardSpecialActions.AppEscapingPolicyFloorMs).
                // Below it a single credited blink can complete the dwell.
                new SliderSpec("d_key",
                    StringTable.Current.T("settings.dwell.keyboard_key.label"),
                    Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1000, 25, 0,
                    () => d.KeyboardKeyDwellMs, v => { d.KeyboardKeyDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.keyboard_key.description") },
                new SliderSpec("d_pred",
                    StringTable.Current.T("settings.dwell.prediction.label"),
                    Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1000, 25, 0,
                    () => d.KeyboardPredictionDwellMs, v => { d.KeyboardPredictionDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.prediction.description") },
                new SliderSpec("d_rad",
                    StringTable.Current.T("settings.dwell.fixation_radius.label"), 20, 120, 5, 0,
                    () => d.FixationRadiusPixels, v => { d.FixationRadiusPixels = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.fixation_radius.description") },
                new SliderSpec("d_cooldown",
                    StringTable.Current.T("settings.dwell.cooldown.label"), 100, 1000, 50, 0,
                    () => d.DwellCooldownMs, v => { d.DwellCooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.cooldown.description") },
                new ToggleSpec("d_cue_sound",
                    StringTable.Current.T("settings.dwell.cue_sound.label"),
                    () => d.CueEnabled, v => { d.CueEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.cue_sound.description") },
                new ButtonSpec("d_reset",
                    StringTable.Current.T("settings.dwell.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.dwell.reset.message"), () =>
                    {
                        var def = new Core.Settings.DwellSettings();
                        d.FixationDetectionMs = def.FixationDetectionMs;
                        d.ActivationButtonDwellMs = def.ActivationButtonDwellMs;
                        d.ActionMenuDwellMs = def.ActionMenuDwellMs;
                        d.ManualSelectionActionMenuDwellMs = def.ManualSelectionActionMenuDwellMs;
                        d.ToolbarButtonDwellMs = def.ToolbarButtonDwellMs;
                        d.KeyboardKeyDwellMs = def.KeyboardKeyDwellMs;
                        d.KeyboardPredictionDwellMs = def.KeyboardPredictionDwellMs;
                        d.FixationRadiusPixels = def.FixationRadiusPixels;
                        d.DwellCooldownMs = def.DwellCooldownMs;
                        d.CueEnabled = def.CueEnabled;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
