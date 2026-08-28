namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class ActionMenuPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ActionMenuPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var a = _ctx.Settings.ActionMenu;
        var rows = new List<SettingsRowSpec>();

        // The tab's one real setting: which actions appear in the ring. Every InteractionAction
        // gets a toggle — including ScrollLeft/ScrollRight, which ship OFF — so the list is fully
        // the user's. The ring reads VisibleActions at menu-open time, so a toggle applies on the
        // next open, no restart (the same live-apply every other toggle here relies on).
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
                            _ctx.ShowModal(
                                $"The first layer holds {Core.Settings.ActionMenuSettings.MaxPrimaryActions} actions. "
                                + "Turn one off before adding another.",
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

        return [new PageSpec(rows)];
    }

    private static string FriendlyName(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "Left Click",
        InteractionAction.RightClick => "Right Click",
        InteractionAction.DoubleClick => "Double Click",
        InteractionAction.Drag => "Drag",
        InteractionAction.ScrollUp => "Scroll Up",
        InteractionAction.ScrollDown => "Scroll Down",
        InteractionAction.ScrollLeft => "Scroll Left",
        InteractionAction.ScrollRight => "Scroll Right",
        InteractionAction.ScrollMode => "Scroll Mode",
        InteractionAction.PinNote => "Pin Note",
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
        InteractionAction.Drag => "Grabs at that point, then follows your gaze.",
        InteractionAction.ScrollUp => "Scrolls up from the point you chose.",
        InteractionAction.ScrollDown => "Scrolls down from the point you chose.",
        InteractionAction.ScrollLeft => "Scrolls left from the point you chose.",
        InteractionAction.ScrollRight => "Scrolls right from the point you chose.",
        InteractionAction.ScrollMode => "Continuous scrolling, steered by where you look.",
        InteractionAction.PinNote => "Opens a note to type at the point you chose.",
        _ => null
    };
}
