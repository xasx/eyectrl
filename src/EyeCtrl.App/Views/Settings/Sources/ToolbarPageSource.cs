namespace EyeCtrl.App.Views.Settings;

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
                new ToggleSpec("t_dock", "Dock to Screen Edge",
                    () => t.IsDocked, v => { t.IsDocked = v; _ctx.ApplyAndSave(); })
                    { Description = "Docks the toolbar to an edge; with an eye tracker it auto-hides." },
                new ComboBoxSpec("t_edge", "Docked Edge",
                    new[] { "Top", "Bottom", "Left", "Right" }, null,
                    () => t.DockedEdge, v => { t.DockedEdge = v; _ctx.ApplyAndSave(); })
                    { Description = "Which screen edge the toolbar attaches to." },
                new SliderSpec("t_size", "Button Size", 32, 120, 4, 0,
                    () => t.Size, v => { t.Size = v; _ctx.ApplyAndSave(); })
                    { Description = "Size of each button; bigger ones are easier to aim at." },
                new ButtonSpec("t_reset", "Reset Toolbar Settings",
                    () => _ctx.ShowModal("Reset Toolbar settings to defaults?", () =>
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
