namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;

internal sealed class DwellPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public DwellPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    // Wave D D4: ONE page for every dwell or look-time row, grouped by where the owner meets
    // it. The declared page split is advisory (the shell re-chunks by height); it exists only
    // to keep the reset button on the second source page. Simple-mode curation is unchanged:
    // the group headings are not Simple, so the filter drops them and the curated rows keep
    // their previous order.
    public IReadOnlyList<PageSpec> Build()
    {
        var d = _ctx.Settings.Dwell;
        return
        [
            new PageSpec([
                GroupHeading("settings.dwell.group.activation.label"),
                new SliderSpec("d_act",
                    StringTable.Current.T("settings.dwell.activation.label"), 200, 2000, 50, 0,
                    () => d.ActivationButtonDwellMs, v => { d.ActivationButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.activation.description"), Simple = true },
                // Round button B3: how long a rest lasts before the activator appears (500 = at the
                // settle, as before). Right next to the activator's own dwell; live like it.
                new SliderSpec("d_act_show",
                    StringTable.Current.T("settings.dwell.activation_show_after.label"), 500, 2500, 100, 0,
                    () => d.OfferShowAfterMs, v => { d.OfferShowAfterMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.activation_show_after.description"), Simple = true },

                GroupHeading("settings.dwell.group.menus.label"),
                new SliderSpec("d_menu",
                    StringTable.Current.T("settings.dwell.action_menu.label"), 200, 2000, 50, 0,
                    () => d.ActionMenuDwellMs, v => { d.ActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.action_menu.description"), Simple = true },
                new SliderSpec("d_ManualSelection",
                    StringTable.Current.T("settings.dwell.manual_selection.label"), 200, 2000, 50, 0,
                    () => d.ManualSelectionActionMenuDwellMs, v => { d.ManualSelectionActionMenuDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.manual_selection.description") },
                new SliderSpec("d_manual_side",
                    StringTable.Current.T("settings.dwell.manual_selection_side.label"), 200, 2000, 50, 0,
                    () => d.ManualSelectionSideButtonDwellMs, v => { d.ManualSelectionSideButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.manual_selection_side.description") },

                GroupHeading("settings.dwell.group.bars.label"),
                new SliderSpec("d_tool",
                    StringTable.Current.T("settings.dwell.toolbar.label"), 200, 2000, 50, 0,
                    () => d.ToolbarButtonDwellMs, v => { d.ToolbarButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.toolbar.description"), Simple = true },
                // Moved here from General (Wave D D4): a button look time belongs with the rest.
                new SliderSpec("g_restart_notice",
                    StringTable.Current.T("settings.general.restart_notice.label"), 0, 3000, 200, 0,
                    () => d.RestartNoticeDwellMs, v => { d.RestartNoticeDwellMs = v < 200 ? 0 : (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.general.restart_notice.description") },
                // Moved here from Advanced (Wave D D4): the backdrop's own hide button.
                new SliderSpec("av_backdrop_hide",
                    StringTable.Current.T("settings.advanced.backdrop_hide.label"), 200, 2000, 50, 0,
                    () => d.BackdropHideButtonDwellMs, v => { d.BackdropHideButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_hide.description") },
                // S4-A: the Task Switcher's tiles (window tiles + "Show desktop") look-time, right
                // after the toolbar row it is bounded by — the effective dwell the switcher uses
                // is max with ToolbarButtonDwellMs (TaskSwitcherViewModel.EffectiveTileDwellMs),
                // so a slower toolbar slows the tiles; the ✕ disc stays on the toolbar value.
                // Live like its neighbours.
                new SliderSpec("d_ts_tile",
                    StringTable.Current.T("settings.dwell.task_switcher_tile.label"), 500, 2500, 100, 0,
                    () => d.TaskSwitcherTileDwellMs, v => { d.TaskSwitcherTileDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.task_switcher_tile.description") },

                GroupHeading("settings.dwell.group.typing.label"),
                // 300 is the keyboard family's blink-credit floor, shared with the Core guard and the
                // adaptive clamp (Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs; the bound
                // and its derivation live beside KeyboardSpecialActions.AppEscapingPolicyFloorMs).
                // Below it a single credited blink can complete the dwell.
                new SliderSpec("d_key",
                    StringTable.Current.T("settings.dwell.keyboard_key.label"),
                    Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1500, 25, 0,
                    () => d.KeyboardKeyDwellMs, v => { d.KeyboardKeyDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.keyboard_key.description"), Simple = true },
                new SliderSpec("d_pred",
                    StringTable.Current.T("settings.dwell.prediction.label"),
                    Core.Adaptation.AdaptationDwellRanges.KeyboardMinDwellMs, 1500, 25, 0,
                    () => d.KeyboardPredictionDwellMs, v => { d.KeyboardPredictionDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.prediction.description"), Simple = true },
            ]),
            new PageSpec([
                GroupHeading("settings.dwell.group.detection.label"),
                new SliderSpec("d_fix",
                    StringTable.Current.T("settings.dwell.fixation.label"), 100, 1000, 25, 0,
                    () => d.FixationDetectionMs, v => { d.FixationDetectionMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.fixation.description") },
                new SliderSpec("d_rad",
                    StringTable.Current.T("settings.dwell.fixation_radius.label"), 10, 200, 5, 0,
                    () => d.FixationRadiusPixels, v => { d.FixationRadiusPixels = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.fixation_radius.description"), Simple = true },
                new SliderSpec("d_cooldown",
                    StringTable.Current.T("settings.dwell.cooldown.label"), 100, 5000, 50, 0,
                    () => d.DwellCooldownMs, v => { d.DwellCooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.cooldown.description") },

                GroupHeading("settings.dwell.group.drag.label"),
                // Moved here from Advanced (Wave D D4): the drag's settle dwell is a look time.
                new SliderSpec("av_drag_settle",
                    StringTable.Current.T("settings.advanced.drag_settle.label"), 200, 2000, 50, 0,
                    () => _ctx.Settings.Drag.SettleDwellMs, v => { _ctx.Settings.Drag.SettleDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_settle.description") },
                new SliderSpec("av_drag_release",
                    StringTable.Current.T("settings.advanced.drag_release.label"), 200, 2000, 50, 0,
                    () => d.DragReleaseButtonDwellMs, v => { d.DragReleaseButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_release.description") },
                new SliderSpec("av_drag_right_edge",
                    StringTable.Current.T("settings.advanced.drag_right_edge.label"), 200, 2000, 50, 0,
                    () => d.DragRightEdgeButtonDwellMs, v => { d.DragRightEdgeButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_right_edge.description") },
                new SliderSpec("av_scroll_stop",
                    StringTable.Current.T("settings.advanced.scroll_stop.label"), 200, 2000, 50, 0,
                    () => d.ScrollStopButtonDwellMs, v => { d.ScrollStopButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.scroll_stop.description") },
                new SliderSpec("av_repeat_dwell",
                    StringTable.Current.T("settings.advanced.repeat_dwell.label"), 200, 2000, 50, 0,
                    () => _ctx.Settings.Repeat.DwellMs, v => { _ctx.Settings.Repeat.DwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.repeat_dwell.description") },
                new SliderSpec("av_repeat_stop",
                    StringTable.Current.T("settings.advanced.repeat_stop.label"), 200, 2000, 50, 0,
                    () => _ctx.Settings.Repeat.StopButtonDwellMs, v => { _ctx.Settings.Repeat.StopButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.repeat_stop.description") },

                GroupHeading("settings.dwell.group.pause.label"),
                new SliderSpec("d_resume",
                    StringTable.Current.T("settings.dwell.resume.label"), 500, 3000, 50, 0,
                    () => d.ResumeDwellMs, v => { d.ResumeDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.resume.description") },
                new SliderSpec("d_pause_grace",
                    StringTable.Current.T("settings.dwell.pause_grace.label"), 0, 1000, 50, 0,
                    () => d.DwellPauseGraceMs, v => { d.DwellPauseGraceMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.pause_grace.description"), Simple = true },
                // Moved here from Eye Care (Wave D D4): a button look time. 0 = follow
                // ActivationButtonDwellMs live; the 200 ms step means the 0-then-200 hole in the
                // guard is never landed on by the stepper, and the setter snaps below 200 to 0.
                new SliderSpec("ec_banner_dwell",
                    StringTable.Current.T("settings.eyecare.banner_dwell.label"), 0, 3000, 200, 0,
                    () => d.EyeCareBannerDwellMs, v => { d.EyeCareBannerDwellMs = v < 200 ? 0 : (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.eyecare.banner_dwell.description") },
                // Moved here from Advanced (Wave D D4): the dwell external apps get.
                new SliderSpec("av_ext_dwell",
                    StringTable.Current.T("settings.advanced.external_hold.label"), 200, 3000, 50, 0,
                    () => _ctx.Settings.ExternalDwell.DefaultDwellMs, v => { _ctx.Settings.ExternalDwell.DefaultDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_hold.description") },

                GroupHeading("settings.dwell.group.sounds.label"),
                new ToggleSpec("d_cue_sound",
                    StringTable.Current.T("settings.dwell.cue_sound.label"),
                    () => d.CueEnabled, v => { d.CueEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.cue_sound.description"), Simple = true },
                // Wave C C1: the visual sibling of d_cue_sound, on the same announcement. All mode
                // only (not Simple): it is on by default and needs no curation.
                new ToggleSpec("d_ring_flash",
                    StringTable.Current.T("settings.dwell.ring_flash.label"),
                    () => d.RingFiredFlashEnabled, v => { d.RingFiredFlashEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.ring_flash.description") },
                // Wave B B3: a distinct sound from d_cue_sound — a real tracker disconnect, not a
                // completed dwell. Simple by decision (the owner hears it by default and must be
                // able to turn it off); the curation pin below records it.
                new ToggleSpec("d_status_chime",
                    StringTable.Current.T("settings.dwell.status_chime.label"),
                    () => d.StatusChimeEnabled, v => { d.StatusChimeEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.dwell.status_chime.description"), Simple = true },
                new ButtonSpec("d_reset",
                    StringTable.Current.T("settings.dwell.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.dwell.reset.message"), () =>
                    {
                        var def = new Core.Settings.DwellSettings();
                        var drag = new Core.Settings.DragSettings();
                        var repeat = new Core.Settings.RepeatSettings();
                        var ext = new Core.Settings.ExternalDwellSettings();
                        d.FixationDetectionMs = def.FixationDetectionMs;
                        d.FixationRadiusPixels = def.FixationRadiusPixels;
                        d.ActivationButtonDwellMs = def.ActivationButtonDwellMs;
                        d.OfferShowAfterMs = def.OfferShowAfterMs;
                        d.ActionMenuDwellMs = def.ActionMenuDwellMs;
                        d.ManualSelectionActionMenuDwellMs = def.ManualSelectionActionMenuDwellMs;
                        d.ManualSelectionSideButtonDwellMs = def.ManualSelectionSideButtonDwellMs;
                        d.ToolbarButtonDwellMs = def.ToolbarButtonDwellMs;
                        d.TaskSwitcherTileDwellMs = def.TaskSwitcherTileDwellMs;
                        d.KeyboardKeyDwellMs = def.KeyboardKeyDwellMs;
                        d.KeyboardPredictionDwellMs = def.KeyboardPredictionDwellMs;
                        d.DwellCooldownMs = def.DwellCooldownMs;
                        d.DwellPauseGraceMs = def.DwellPauseGraceMs;
                        d.ResumeDwellMs = def.ResumeDwellMs;
                        d.RestartNoticeDwellMs = def.RestartNoticeDwellMs;
                        d.BackdropHideButtonDwellMs = def.BackdropHideButtonDwellMs;
                        d.DragReleaseButtonDwellMs = def.DragReleaseButtonDwellMs;
                        d.DragRightEdgeButtonDwellMs = def.DragRightEdgeButtonDwellMs;
                        d.ScrollStopButtonDwellMs = def.ScrollStopButtonDwellMs;
                        d.EyeCareBannerDwellMs = def.EyeCareBannerDwellMs;
                        d.CueEnabled = def.CueEnabled;
                        d.RingFiredFlashEnabled = def.RingFiredFlashEnabled;
                        d.StatusChimeEnabled = def.StatusChimeEnabled;
                        // The moved drag/repeat/external look times keep the page's reset complete
                        // without touching those pages' non-time rows (radius, colour, enabled).
                        _ctx.Settings.Drag.SettleDwellMs = drag.SettleDwellMs;
                        _ctx.Settings.Repeat.DwellMs = repeat.DwellMs;
                        _ctx.Settings.Repeat.StopButtonDwellMs = repeat.StopButtonDwellMs;
                        _ctx.Settings.ExternalDwell.DefaultDwellMs = ext.DefaultDwellMs;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
            ]),
        ];
    }

    /// <summary>
    /// One group heading on the page. Not <c>Simple</c>: the curated mode drops it, so the
    /// owner's simple sequence is unchanged, and the heading is a name for the rows below it
    /// rather than a setting of its own.
    /// </summary>
    private static LabelSpec GroupHeading(string key) =>
        new(() => StringTable.Current.T(key));
}
