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
                    { Description = "All three are dark; Contrast is the boldest." },
                new ToggleSpec("g_boot", "Start on Windows Boot",
                    () => s.General.StartOnBoot, v => { s.General.StartOnBoot = v; _ctx.ApplyAndSave(); })
                    { Description = "Opens EyeCtrl automatically when you sign in." },
                new ToggleSpec("g_pause", "Start Paused",
                    () => s.General.StartPaused, v => { s.General.StartPaused = v; _ctx.ApplyAndSave(); })
                    { Description = "Starts with tracking paused until you switch it on." },
                new ToggleSpec("g_track", "Show Track Status",
                    () => s.TrackStatus.Enabled, v => { s.TrackStatus.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "A corner box showing live eye-tracker status." },
                new ButtonSpec("g_restart", "↻ Restart Now", OnRestart, ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("g_reset", "Reset General Settings",
                    () => _ctx.ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new Core.Settings.GeneralSettings();
                        s.General.StartOnBoot = g.StartOnBoot;
                        s.General.StartPaused = g.StartPaused;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("g_resetall", "\u26A0 Reset ALL Settings",
                    () => _ctx.ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        _ctx.ViewModel.Settings.ResetAllSettings();
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
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
