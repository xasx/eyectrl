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
                }));
        }

        rows.Add(new ButtonSpec("am_reset", "Reset Action Menu Settings",
            () => _ctx.ShowModal("Reset Action Menu settings to defaults?", () =>
            {
                a.VisibleActions = new List<InteractionAction>(new Core.Settings.ActionMenuSettings().VisibleActions);
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
}
