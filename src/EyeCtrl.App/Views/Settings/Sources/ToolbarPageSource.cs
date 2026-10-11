namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;

internal sealed class ToolbarPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public ToolbarPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var t = _ctx.Settings.Toolbar;
        return
        [
            new PageSpec([
                new ToggleSpec("t_dock",
                    StringTable.Current.T("settings.toolbar.dock.label"),
                    () => t.IsDocked, v => { t.IsDocked = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.toolbar.dock.description") },
                new ComboBoxSpec("t_edge",
                    StringTable.Current.T("settings.toolbar.edge.label"),
                    [
                        StringTable.Current.T("settings.toolbar.edge.option.top"),
                        StringTable.Current.T("settings.toolbar.edge.option.bottom"),
                        StringTable.Current.T("settings.toolbar.edge.option.left"),
                        StringTable.Current.T("settings.toolbar.edge.option.right"),
                    ],
                    ["Top", "Bottom", "Left", "Right"],
                    () => t.DockedEdge, v => { t.DockedEdge = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.toolbar.edge.description") },
                new SliderSpec("t_size",
                    StringTable.Current.T("settings.toolbar.size.label"), 40, 120, 4, 0,
                    () => t.Size, v => { t.Size = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.toolbar.size.description") },
                new ButtonSpec("t_reset",
                    StringTable.Current.T("settings.toolbar.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.toolbar.reset.message"), () =>
                    {
                        var def = new Core.Settings.ToolbarSettings();
                        t.IsDocked = def.IsDocked;
                        t.DockedEdge = def.DockedEdge;
                        t.Size = def.Size;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
