namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.ActionMenu;
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
    /// Default honesty copy for the preview page (adoption 2026-09-30; user-changeable later):
    /// the miniature looks dwellable and is not, and the runtime only shows ring labels while a
    /// target is being dwelled.
    /// </summary>
    internal const string RingPreviewCaptionNotAControl = "preview, not a control";

    /// <inheritdoc cref="RingPreviewCaptionNotAControl"/>
    internal const string RingPreviewCaptionLabels = "runtime shows labels only while you dwell";

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
            new RingJumpSpec(RingJumpRowId, "View ring preview", JumpToRingPreview,
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
                $"{FriendlyName(action)} on first layer",
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
                                $"The first layer holds {Core.Settings.ActionMenuSettings.MaxPrimaryActions} actions. "
                                + "Turn off another action's \"on first layer\" switch to free a slot.",
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

        rows.Add(new ButtonSpec("am_reset", "Reset Action Menu Settings",
            () => _ctx.ShowModal("Reset Action Menu settings to defaults?", () =>
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
                RingLabelSource.English),
            RingPreviewCaptionNotAControl,
            RingPreviewCaptionLabels));

        return [new PageSpec(rows)];
    }

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
        InteractionAction.LeftClick => "Left Click",
        InteractionAction.RightClick => "Right Click",
        InteractionAction.DoubleClick => "Double Click",
        InteractionAction.MiddleClick => "Middle Click",
        InteractionAction.Drag => "Drag",
        InteractionAction.ScrollUp => "Scroll Up",
        InteractionAction.ScrollDown => "Scroll Down",
        InteractionAction.ScrollLeft => "Scroll Left",
        InteractionAction.ScrollRight => "Scroll Right",
        InteractionAction.ScrollMode => "Scroll Mode",
        InteractionAction.PinNote => "Pin Note",
        InteractionAction.Keyboard => "Keyboard",
        _ => action.ToString()
    };

    /// <summary>
    /// Description for a "on first layer" row. One sentence per action rather than one shared
    /// string, because SettingsRowDescriptionCompletenessTests treats a bare row as a failure and
    /// the row says WHERE the action sits, not what it does — the visibility row above it already
    /// carries the what.
    /// </summary>
    private static string PrimaryDescription(InteractionAction action) =>
        $"Shows {FriendlyName(action)} straight away instead of behind More. Four fit.";

    // Copy verbatim from the spec inventory. Unknown enum members return null — the
    // completeness gate (SettingsRowDescriptionCompletenessTests) fails on the bare row by
    // name, which is the loud failure that matters. Throwing here instead would take the
    // settings window down on a future enum addition, and this app deliberately terminates on
    // dispatcher exceptions (the same crash class the dwell-slider fixes close).
    private static string? ActionDescription(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "One left click at the point you chose.",
        InteractionAction.RightClick => "One right click at the point you chose.",
        InteractionAction.DoubleClick => "Two quick clicks at the point you chose.",
        InteractionAction.MiddleClick => "One middle click at the point you chose.",
        InteractionAction.Drag => "Grabs at that point, then follows your gaze.",
        InteractionAction.ScrollUp => "Scrolls up from the point you chose.",
        InteractionAction.ScrollDown => "Scrolls down from the point you chose.",
        InteractionAction.ScrollLeft => "Scrolls left from the point you chose.",
        InteractionAction.ScrollRight => "Scrolls right from the point you chose.",
        InteractionAction.ScrollMode => "Continuous scrolling, steered by where you look.",
        InteractionAction.PinNote => "Opens a note to type at the point you chose.",
        InteractionAction.Keyboard => "Opens the gaze keyboard for typing.",
        _ => null
    };
}
