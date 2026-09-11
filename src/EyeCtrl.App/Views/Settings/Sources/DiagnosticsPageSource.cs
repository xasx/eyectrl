namespace EyeCtrl.App.Views.Settings;

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
                new ToggleSpec("dg_record_raw", "Record Raw Gaze (from next start)",
                    () => d.RecordRawGaze, v => { d.RecordRawGaze = v; _ctx.ApplyAndSave(); })
                    { Description = "Save every gaze sample to a file for analysis. Off by default; turn it back off when you are done." },
                new LabelSpec(() =>
                    "A recording is a trace of where you looked and when — nothing about what you were "
                    + "looking at. It stays on this computer, in your EyeCtrl folder, until you delete it."),
                new ToggleSpec("dg_ui_instruments", "Measure UI Timing (from next start)",
                    () => d.MeasureUiInstruments, v => { d.MeasureUiInstruments = v; _ctx.ApplyAndSave(); })
                    { Description = "Time how gaze reaches the screen. Records no gaze positions; writes only summary lines to the log." },
                new ButtonSpec("dg_open_folder", "Open Recordings Folder",
                    () => _onOpenRecordingFolder?.Invoke(),
                    ButtonKind.Default, Width: 300, Height: 100)
                    { Description = "Show gaze-recording.csv and its ground-truth file in Explorer." },
                new ButtonSpec("dg_reset", "Reset Diagnostics Settings",
                    () => _ctx.ShowModal("Turn both diagnostics off?", () =>
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
