namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Localization;

/// <summary>
/// The two measurement instruments, exposed where a gaze user can reach them.
/// <para>
/// Until this page existed both flags were reachable only by hand-editing <c>settings.json</c> — which
/// for this application's user is not a hard path but an impossible one: the app IS their input, so
/// editing its config file requires either the app to be running (whereupon it overwrites the edit on
/// exit) or somebody else at the keyboard. A diagnostic nobody can turn on measures nothing.
/// </para>
/// <para>
/// Both toggles take effect at the NEXT START, and the labels say so rather than leaving the user to
/// discover it. That is not an oversight in the wiring: the recorder decorates the gaze provider once
/// at composition, and a recording begun halfway through a session has no header describing the
/// session it is half of.
/// </para>
/// </summary>
internal sealed class DiagnosticsPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    private readonly Action? _onOpenRecordingFolder;

    public DiagnosticsPageSource(SettingsPageContext ctx, Action? onOpenRecordingFolder = null)
    {
        _ctx = ctx;
        _onOpenRecordingFolder = onOpenRecordingFolder;
    }

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var d = _ctx.Settings.Diagnostics;
        return
        [
            new PageSpec([
                new ToggleSpec("dg_record_raw",
                    StringTable.Current.T("settings.diagnostics.record_raw.label"),
                    () => d.RecordRawGaze, v => { d.RecordRawGaze = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.diagnostics.record_raw.description") },
                new LabelSpec(() => StringTable.Current.T("settings.diagnostics.record_raw.note")),
                new ToggleSpec("dg_ui_instruments",
                    StringTable.Current.T("settings.diagnostics.ui_timing.label"),
                    () => d.MeasureUiInstruments, v => { d.MeasureUiInstruments = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.diagnostics.ui_timing.description") },
                new ButtonSpec("dg_open_folder",
                    StringTable.Current.T("settings.diagnostics.open_folder.label"),
                    () => _onOpenRecordingFolder?.Invoke(),
                    ButtonKind.Default, Width: 300, Height: 100)
                    { Description = StringTable.Current.T("settings.diagnostics.open_folder.description") },
                new ButtonSpec("dg_reset",
                    StringTable.Current.T("settings.diagnostics.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.diagnostics.reset.message"), () =>
                    {
                        var defaults = new Core.Settings.DiagnosticsSettings();
                        d.RecordRawGaze = defaults.RecordRawGaze;
                        d.MeasureUiInstruments = defaults.MeasureUiInstruments;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
