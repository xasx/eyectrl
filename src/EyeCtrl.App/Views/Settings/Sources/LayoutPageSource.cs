namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

internal sealed class LayoutPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public LayoutPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("lay_pos", "Position",
                    new[] { "At Gaze", "At Center" }, null,
                    () => s.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { s.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; _ctx.ApplyAndSave(); }),
                new ButtonSpec("lay_reset", "Reset Layout Settings",
                    () => _ctx.ShowModal("Reset Layout settings to defaults?", () =>
                    {
                        s.ViewPosition = new Core.Settings.EyeCtrlSettings().ViewPosition;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
