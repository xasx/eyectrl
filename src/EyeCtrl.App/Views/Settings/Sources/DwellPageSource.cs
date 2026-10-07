namespace EyeCtrl.App.Views.Settings;

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
                new SliderSpec("d_fix", "Fixation Detection (ms)", 100, 500, 25, 0,
                    () => d.FixationDetectionMs, v => { d.FixationDetectionMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long your gaze must hold steady before a dwell can start." },
                new SliderSpec("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
                    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell for the button that appears at your gaze." },
                new SliderSpec("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
                    () => d.ActionMenuDwellMs, v => { d.ActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long to rest on an action in the ring." },
                new SliderSpec("d_ManualSelection", "Manual Selection Action Menu (ms)", 200, 1500, 50, 0,
                    () => d.ManualSelectionActionMenuDwellMs, v => { d.ManualSelectionActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell for the action ring inside manual selection." },
            ]),
            new PageSpec([
                new SliderSpec("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
                    () => d.ToolbarButtonDwellMs, v => { d.ToolbarButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long to rest on EyeCtrl's own buttons." },
                // 300 is the keyboard family's blink-credit floor, shared with the Core guard and the
                // adaptive clamp (Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs; the bound
                // and its derivation live beside KeyboardSpecialActions.AppEscapingPolicyFloorMs).
                // Below it a single credited blink can complete the dwell.
                new SliderSpec("d_key", "Keyboard Key (ms)", Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1000, 25, 0,
                    () => d.KeyboardKeyDwellMs, v => { d.KeyboardKeyDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long to rest on a key before it fires. Keys that can leave the app or destroy work use 600 ms." },
                new SliderSpec("d_pred", "Prediction (ms)", Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1000, 25, 0,
                    () => d.KeyboardPredictionDwellMs, v => { d.KeyboardPredictionDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell for the word suggestions above the keyboard." },
                new SliderSpec("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
                    () => d.FixationRadiusPixels, v => { d.FixationRadiusPixels = v; _ctx.ApplyAndSave(); })
                    { Description = "How far your gaze may drift within a fixation." },
                new SliderSpec("d_cooldown", "Dwell Cooldown (ms)", 100, 1000, 50, 0,
                    () => d.DwellCooldownMs, v => { d.DwellCooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long a new dwell must wait after an activation." },
                new ToggleSpec("d_cue_sound", "Dwell Completion Sound",
                    () => d.CueEnabled, v => { d.CueEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Plays a soft tone when a dwell activates on any EyeCtrl surface — buttons, menus, settings. Keys keep their own click sound." },
                new ButtonSpec("d_reset", "Reset Dwell Settings",
                    () => _ctx.ShowModal("Reset Dwell settings to defaults?", () =>
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
