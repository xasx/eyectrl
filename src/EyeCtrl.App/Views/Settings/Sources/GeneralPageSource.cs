namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

internal sealed class GeneralPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public GeneralPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings;
        return
        [
            new PageSpec([
                new ComboBoxSpec("g_theme", "Theme (restart to apply)",
                    Core.Settings.ThemeSettings.ValidNames, null,
                    () => s.Theme.Name, v => { s.Theme.Name = v; _ctx.ApplyAndSave(); })
                    { Description = "Daylight is light, for bright rooms; Contrast is the boldest." },
                new ToggleSpec("g_boot", "Start on Windows Boot",
                    () => s.General.StartOnBoot, v => { s.General.StartOnBoot = v; _ctx.ApplyAndSave(); })
                    { Description = "Opens EyeCtrl automatically when you sign in." },
                new ToggleSpec("g_pause", "Start Paused",
                    () => s.General.StartPaused, v => { s.General.StartPaused = v; _ctx.ApplyAndSave(); })
                    { Description = "Starts with tracking paused until you switch it on." },
                new ToggleSpec("g_track", "Show Track Status",
                    () => s.TrackStatus.Enabled, v => { s.TrackStatus.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "A corner box showing live eye-tracker status." },
                .. SupervisionRows(),
                new ButtonSpec("g_restart", "↻ Restart Now", OnRestart, ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("g_reset", "Reset General Settings",
                    // N4 (round-1 review): the reset now includes the theme, which only
                    // applies after a restart — the modal says so, like the theme row itself.
                    () => _ctx.ShowModal("Reset General settings to defaults?\n"
                        + "A changed theme applies after a restart.", () =>
                    {
                        // Resets every settings row the page renders, not one section (issue
                        // #164): the rows bind three different sub-settings objects, and the
                        // original handler restored only the GeneralSettings pair — leaving
                        // the Theme and TrackStatus rows the button sits next to untouched,
                        // the exact UX expectation that filed #157.
                        var g = new Core.Settings.GeneralSettings();
                        s.General.StartOnBoot = g.StartOnBoot;
                        s.General.StartPaused = g.StartPaused;
                        var t = new Core.Settings.ThemeSettings();
                        s.Theme.Name = t.Name;
                        var tr = new Core.Settings.TrackStatusSettings();
                        s.TrackStatus.Enabled = tr.Enabled;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("g_resetall", "\u26A0 Reset ALL Settings",
                    () => _ctx.ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        _ctx.ViewModel.Settings.ResetAllSettings();
                        // ResetAllSettings copies every default by reflection, including
                        // the Midnight-absolute mark colours — the third path that can
                        // reopen the cross-theme carry-over at runtime (round 3, R2;
                        // the other two were the cursor and advanced resets).
                        EyeCtrl.App.Services.ThemeStartup.MigrateHaloedMarks(
                            EyeCtrl.Controls.Theme.Tokens.Current, _ctx.Settings);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }

    /// <summary>
    /// The supervision row group (ticket 04): the dwell toggle plus its live status line.
    /// Not available (no Supervisor exe): a single note with the reason, in place of the toggle —
    /// the Cursor page's unavailable-row-swaps-to-a-note convention. Null service (headless or
    /// otherwise degraded host): no rows at all.
    /// </summary>
    private IEnumerable<SettingsRowSpec> SupervisionRows()
    {
        var supervision = _ctx.Supervision;
        if (supervision is null)
            yield break;

        if (!supervision.IsAvailable)
        {
            yield return new LabelSpec(() => "Automatic Crash Recovery is unavailable: "
                + "the EyeCtrl Supervisor is not installed on this PC.");
            yield break;
        }

        yield return new ToggleSpec("g_supervision", "Automatic Crash Recovery",
            () => supervision.Enabled,
            v =>
            {
                if (v) supervision.RequestEnable();
                else supervision.RequestDisable();
            })
            { Description = "If EyeCtrl stops responding or crashes, it restarts automatically; "
                + "if it keeps failing, TD Control opens instead." };

        // Ticket 07: enabled but no Supervisor answering (e.g. after a reboot, before the startup
        // ensure lands, or after a Supervisor exit) — offer the retry, sized like the Restart
        // button for aimability, and keep the toggle On (the row never flips the disk flag itself).
        if (supervision.Enabled && !supervision.IsSupervisorConnected)
        {
            yield return new ButtonSpec("g_supervision_retry", "\u21BB Start Supervisor",
                () => supervision.RequestEnsure(), ButtonKind.Default, Width: 300, Height: 100);
        }

        yield return new LabelSpec(() => supervision.StatusText);
    }

    private void OnRestart()
    {
        // Only offer what is installed and not already running. The multi-choice modal is built
        // directly on the ViewModel (the same object ShowModal's two-button helper wraps).
        if (_ctx.Restart.ShouldOfferSupervisor())
        {
            _ctx.ViewModel.ShowModal(
                "Restart EyeCtrl now?\n"
                + "A supervisor is installed. It restarts EyeCtrl if it ever fails, and can recover "
                + "a restart that goes wrong.\nStarting it turns supervision on and leaves it on.",
                [
                    new ModalChoice("restart_supervised", "↻ Restart with supervisor",
                        ModalChoiceKind.Affirmative,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true))),
                    new ModalChoice("restart_plain", "↻ Restart without",
                        ModalChoiceKind.Neutral,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage())),
                    new ModalChoice("restart_cancel", "✕ Cancel",
                        ModalChoiceKind.Negative, null),
                ]);
        }
        else
        {
            _ctx.ShowModal("Restart EyeCtrl now?",
                () => _ctx.Messenger.Send(new RestartAppRequestedMessage()));
        }
    }
}
