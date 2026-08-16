namespace EyeCtrl.App.Views.Settings;

internal sealed class ActionMenuPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ActionMenuPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var a = _ctx.Settings.ActionMenu;
        return
        [
            new PageSpec([
                new ButtonSpec("am_reset", "Reset Action Menu Settings",
                    () => _ctx.ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        a.VisibleActions = new System.Collections.Generic.List<Core.Models.InteractionAction>(
                            new Core.Settings.ActionMenuSettings().VisibleActions);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
