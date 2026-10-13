namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;

internal sealed class ActionMenuPageSource : ISettingsPageSource
{
    /// <summary>
    /// The jump row's dwell id, in the style of the QA picker's per-page <c>qa_back_{page}</c>
    /// ids. It is the ONLY new registered target the preview adds — the ring miniature itself
    /// registers nothing (the painter takes no registrar).
    /// </summary>
    internal const string RingJumpRowId = "am_jump_0";

    /// <summary>
    /// The <c>settings</c> key for the preview's "not a control" honesty caption (T07 moved the
    /// former <c>RingPreviewCaptionNotAControl</c> constant's text into strings.json).
    /// </summary>
    internal const string RingPreviewCaptionNotAControlKey = "settings.action_menu.preview.not_a_control";

    /// <summary>The <c>settings</c> key for the "labels only while you dwell" honesty caption.</summary>
    internal const string RingPreviewCaptionLabelsKey = "settings.action_menu.preview.labels";

    private readonly SettingsPageContext _ctx;
    public ActionMenuPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var a = _ctx.Settings.ActionMenu;
        var rows = new List<SettingsRowSpec>
        {
            // Leads the tab so the preview is one dwell away however many pages the toggles run
            // to. The shell drops it together with the ring when the budget cannot show the
            // preview (see RingJumpSpec), so it never lands on a page with no preview.
            new RingJumpSpec(RingJumpRowId,
                StringTable.Current.T("settings.action_menu.jump.label"), JumpToRingPreview,
                ButtonKind.Default, Width: 360, Height: 100),
        };

        // The tab's one real setting: which actions appear in the ring. Every InteractionAction
        // gets a toggle — including ScrollLeft/ScrollRight and MiddleClick, which ship OFF — so the
        // list is fully the user's. The ring reads VisibleActions at menu-open time, so a toggle
        // applies on the next open, no restart (the same live-apply every other toggle here relies on).
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            var captured = action;
            rows.Add(new ToggleSpec(
                $"am_{action}",
                FriendlyName(action),
                () => a.VisibleActions.Contains(captured),
                on =>
                {
                    if (on && !a.VisibleActions.Contains(captured)) a.VisibleActions.Add(captured);
                    else if (!on) a.VisibleActions.RemoveAll(x => x == captured);
                    _ctx.ApplyAndSave();
                })
                { Description = ActionDescription(captured) });
        }

        // Second axis: of the actions that are ON, which four sit on the ring's first layer. A
        // toggle rather than a picker because the settings surface is dwell-driven — a
        // multi-select control would need its own dwell affordance, and this reuses the row type
        // every other setting here already uses.
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            var captured = action;
            rows.Add(new ToggleSpec(
                $"am_primary_{action}",
                StringTable.Current.T("settings.action_menu.primary.label", FriendlyName(action)),
                () => a.PrimaryActions.Contains(captured),
                on =>
                {
                    if (on)
                    {
                        if (a.PrimaryActions.Contains(captured)) return;
                        if (a.PrimaryActions.Count >= Core.Settings.ActionMenuSettings.MaxPrimaryActions)
                        {
                            // Names the OTHER switch explicitly. "Turn one off" read as the
                            // visibility row above, which does not free a slot: the cap counts
                            // PrimaryActions as stored, and it must — Validate() rejects a stored
                            // list longer than four, Save() throws on that, and Load() answers it
                            // by discarding the whole settings file for defaults.
                            _ctx.ShowModal(
                                StringTable.Current.T(
                                    "settings.action_menu.primary.full",
                                    Core.Settings.ActionMenuSettings.MaxPrimaryActions),
                                () => { });
                            _ctx.RebuildContent();
                            return;
                        }
                        a.PrimaryActions.Add(captured);
                    }
                    else
                    {
                        a.PrimaryActions.RemoveAll(x => x == captured);
                    }
                    _ctx.ApplyAndSave();
                })
                { Description = PrimaryDescription(captured) });
        }

        rows.Add(new ButtonSpec("am_reset",
            StringTable.Current.T("settings.action_menu.reset.label"),
            () => _ctx.ShowModal(StringTable.Current.T("settings.action_menu.reset.message"), () =>
            {
                var defaults = new Core.Settings.ActionMenuSettings();
                a.VisibleActions = new List<InteractionAction>(defaults.VisibleActions);
                a.PrimaryActions = new List<InteractionAction>(defaults.PrimaryActions);
                _ctx.ApplyAndSave();
                _ctx.RebuildContent();
            }),
            // 380, not the 300 most reset buttons use: "Reset Action Menu Settings" measures
            // 311 DIP of text at the live type ramp and clipped its tail inside 300 - 40 padding.
            ButtonKind.Reset, Width: 380, Height: 100));

        // Trailing: the preview is appended AFTER every existing row, so the chunker can never
        // move a toggle — pages holding existing rows are identical with and without it (G8).
        // The captions live inside this row, beside the painting: a preview scaled to fill the
        // height budget leaves no room for a caption row underneath, and a caption the fold
        // clips is not an honesty caption.
        rows.Add(new RingPreviewSpec(
            "am_preview",
            () => RingPlan.Create(a.VisibleActions, a.PrimaryActions, _ctx.Settings.Cursor.CircleSize,
                PreviewRingLabels()),
            StringTable.Current.T(RingPreviewCaptionNotAControlKey),
            StringTable.Current.T(RingPreviewCaptionLabelsKey),
            MoreAnnotationFormat: (label, count) => StringTable.Current.T(
                "settings.action_menu.preview.more_annotation", label, count)));

        return [new PageSpec(rows)];
    }

    /// <summary>
    /// The preview's ring vocabulary, from the <c>settings</c> surface: the action names, Back
    /// and More. Each key mirrors its live-ring twin under <c>overlays</c> (D3) — the settings
    /// preview speaks the settings surface's language while the live ring speaks the overlays
    /// surface's — and the painter is told every label rather than owning any English.
    /// </summary>
    private static RingLabelSource PreviewRingLabels() => new(
        RingLabelFor,
        RingBackLabel(),
        RingMoreLabel());

    /// <summary>The preview's Back slot, from the settings surface (mirrors <c>overlays.ring.back</c>).</summary>
    internal static string RingBackLabel() => StringTable.Current.T("settings.ring.back");

    /// <summary>The preview's More slot, from the settings surface (mirrors <c>overlays.ring.more</c>).</summary>
    internal static string RingMoreLabel() => StringTable.Current.T("settings.ring.more");

    /// <summary>
    /// The ring's name for one action, keyed at the call site so the coverage and unused-key
    /// gates see each literal. Deliberately the ring's vocabulary ("Left click"), not the
    /// settings row's ("Left Click") — <c>RingLabelMap</c> owns that split on the runtime side,
    /// and <c>SettingsSurfaceRingAndPresetLabelsTests</c> pins the two in step.
    /// </summary>
    /// <param name="action">The action the ring slot names.</param>
    /// <returns>The resolved label.</returns>
    internal static string RingLabelFor(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => StringTable.Current.T("settings.ring.leftClick"),
        InteractionAction.RightClick => StringTable.Current.T("settings.ring.rightClick"),
        InteractionAction.DoubleClick => StringTable.Current.T("settings.ring.doubleClick"),
        InteractionAction.MiddleClick => StringTable.Current.T("settings.ring.middleClick"),
        InteractionAction.Drag => StringTable.Current.T("settings.ring.drag"),
        InteractionAction.ScrollUp => StringTable.Current.T("settings.ring.scrollUp"),
        InteractionAction.ScrollDown => StringTable.Current.T("settings.ring.scrollDown"),
        InteractionAction.ScrollLeft => StringTable.Current.T("settings.ring.scrollLeft"),
        InteractionAction.ScrollRight => StringTable.Current.T("settings.ring.scrollRight"),
        InteractionAction.ScrollMode => StringTable.Current.T("settings.ring.scrollMode"),
        InteractionAction.PinNote => StringTable.Current.T("settings.ring.pinNote"),
        InteractionAction.Keyboard => StringTable.Current.T("settings.ring.keyboard"),
        _ => action.ToString(),
    };

    /// <summary>
    /// Jumps to the trailing preview page. The page list is built before the shell sets
    /// <c>TotalPages</c>, so the target is resolved at activation time — the jump row's delegate
    /// runs on a dwell, long after the rebuild that rendered it.
    /// </summary>
    private void JumpToRingPreview()
    {
        var settings = _ctx.ViewModel.Settings;
        settings.CurrentPage = Math.Max(0, settings.TotalPages - 1);
    }

    private static string FriendlyName(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => StringTable.Current.T("settings.action_menu.visible.left_click.label"),
        InteractionAction.RightClick => StringTable.Current.T("settings.action_menu.visible.right_click.label"),
        InteractionAction.DoubleClick => StringTable.Current.T("settings.action_menu.visible.double_click.label"),
        InteractionAction.MiddleClick => StringTable.Current.T("settings.action_menu.visible.middle_click.label"),
        InteractionAction.Drag => StringTable.Current.T("settings.action_menu.visible.drag.label"),
        InteractionAction.ScrollUp => StringTable.Current.T("settings.action_menu.visible.scroll_up.label"),
        InteractionAction.ScrollDown => StringTable.Current.T("settings.action_menu.visible.scroll_down.label"),
        InteractionAction.ScrollLeft => StringTable.Current.T("settings.action_menu.visible.scroll_left.label"),
        InteractionAction.ScrollRight => StringTable.Current.T("settings.action_menu.visible.scroll_right.label"),
        InteractionAction.ScrollMode => StringTable.Current.T("settings.action_menu.visible.scroll_mode.label"),
        InteractionAction.PinNote => StringTable.Current.T("settings.action_menu.visible.pin_note.label"),
        InteractionAction.Keyboard => StringTable.Current.T("settings.action_menu.visible.keyboard.label"),
        _ => action.ToString()
    };

    /// <summary>
    /// Description for a "on first layer" row. One sentence per action rather than one shared
    /// string, because SettingsRowDescriptionCompletenessTests treats a bare row as a failure and
    /// the row says WHERE the action sits, not what it does — the visibility row above it already
    /// carries the what. The action's name arrives as the format's first argument, so German
    /// reorders the sentence around its own name.
    /// </summary>
    private static string PrimaryDescription(InteractionAction action) =>
        StringTable.Current.T("settings.action_menu.primary.description", FriendlyName(action));

    // Copy verbatim from the spec inventory. Unknown enum members return null — the
    // completeness gate (SettingsRowDescriptionCompletenessTests) fails on the bare row by
    // name, which is the loud failure that matters. Throwing here instead would take the
    // settings window down on a future enum addition, and this app deliberately terminates on
    // dispatcher exceptions (the same crash class the dwell-slider fixes close).
    private static string? ActionDescription(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => StringTable.Current.T("settings.action_menu.visible.left_click.description"),
        InteractionAction.RightClick => StringTable.Current.T("settings.action_menu.visible.right_click.description"),
        InteractionAction.DoubleClick => StringTable.Current.T("settings.action_menu.visible.double_click.description"),
        InteractionAction.MiddleClick => StringTable.Current.T("settings.action_menu.visible.middle_click.description"),
        InteractionAction.Drag => StringTable.Current.T("settings.action_menu.visible.drag.description"),
        InteractionAction.ScrollUp => StringTable.Current.T("settings.action_menu.visible.scroll_up.description"),
        InteractionAction.ScrollDown => StringTable.Current.T("settings.action_menu.visible.scroll_down.description"),
        InteractionAction.ScrollLeft => StringTable.Current.T("settings.action_menu.visible.scroll_left.description"),
        InteractionAction.ScrollRight => StringTable.Current.T("settings.action_menu.visible.scroll_right.description"),
        InteractionAction.ScrollMode => StringTable.Current.T("settings.action_menu.visible.scroll_mode.description"),
        InteractionAction.PinNote => StringTable.Current.T("settings.action_menu.visible.pin_note.description"),
        InteractionAction.Keyboard => StringTable.Current.T("settings.action_menu.visible.keyboard.description"),
        _ => null
    };
}
