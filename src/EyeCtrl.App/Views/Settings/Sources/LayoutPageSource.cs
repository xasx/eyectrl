namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;
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
                new ComboBoxSpec("lay_pos",
                    StringTable.Current.T("settings.layout.position.label"),
                    [
                        StringTable.Current.T("settings.layout.position.option.at_gaze"),
                        StringTable.Current.T("settings.layout.position.option.at_center"),
                    ],
                    ["At Gaze", "At Center"],
                    () => s.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { s.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.layout.position.description") },
                new ButtonSpec("lay_reset",
                    StringTable.Current.T("settings.layout.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.layout.reset.message"), () =>
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
