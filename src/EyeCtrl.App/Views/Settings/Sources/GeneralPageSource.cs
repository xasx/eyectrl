namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
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
                // T04/D9: the language row is the FIRST row on page 1. Its reach is pinned by
                // LanguageRowReachTests — a recovery row behind rows the user cannot read is a
                // row the user cannot reach.
                new ComboBoxSpec("g_language",
                    StringTable.Current.T("language-recovery.row.label"),
                    [
                        UiLanguageTokens.Endonym(UiLanguage.System),
                        UiLanguageTokens.Endonym(UiLanguage.German),
                        UiLanguageTokens.Endonym(UiLanguage.English),
                    ],
                    [
                        UiLanguageTokens.ToJson(UiLanguage.System),
                        UiLanguageTokens.ToJson(UiLanguage.German),
                        UiLanguageTokens.ToJson(UiLanguage.English),
                    ],
                    () => UiLanguageTokens.ToJson(s.General.UiLanguage),
                    OnLanguageChosen)
                    { Description = StringTable.Current.T("language-recovery.row.description"), Simple = true },
                .. PendingLanguageNote(),
                new ComboBoxSpec("g_theme",
                    StringTable.Current.T("settings.general.theme.label"),
                    [
                        StringTable.Current.T("settings.general.theme.option.midnight"),
                        StringTable.Current.T("settings.general.theme.option.ember"),
                        StringTable.Current.T("settings.general.theme.option.contrast"),
                    ],
                    Core.Settings.ThemeSettings.ValidNames,
                    () => s.Theme.Name, v => { s.Theme.Name = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.general.theme.description"), Simple = true },
                new ToggleSpec("g_boot",
                    StringTable.Current.T("settings.general.boot.label"),
                    () => s.General.StartOnBoot, v => { s.General.StartOnBoot = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.general.boot.description"), Simple = true },
                new ToggleSpec("g_pause",
                    StringTable.Current.T("settings.general.pause.label"),
                    () => s.General.StartPaused, v => { s.General.StartPaused = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.general.pause.description"), Simple = true },
                new ToggleSpec("g_track",
                    StringTable.Current.T("settings.general.track.label"),
                    () => s.TrackStatus.Enabled, v => { s.TrackStatus.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.general.track.description"), Simple = true },
                .. SupervisionRows(),
                new ButtonSpec("g_restart",
                    StringTable.Current.T("settings.general.restart.label"),
                    OnRestart, ButtonKind.Reset, Width: 300, Height: 100),
                new ButtonSpec("g_reset",
                    StringTable.Current.T("settings.general.reset.label"),
                    // N4 (round-1 review): the reset now includes the theme, which only
                    // applies after a restart — the modal says so, like the theme row itself.
                    () => _ctx.ShowModal(StringTable.Current.T("settings.general.reset.message"), () =>
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
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
                new ButtonSpec("g_resetall",
                    StringTable.Current.T("settings.general.resetall.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.general.resetall.message"), () =>
                    {
                        _ctx.ViewModel.Settings.ResetAllSettings();
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }

    /// <summary>
    /// The bilingual "Wird nach Neustart wirksam / Takes effect after restart" note, present only
    /// while the saved choice resolves differently from the language this process started with.
    /// A conditional ROW rather than a row Description: a not-pending build draws nothing at all,
    /// and the note's presence is re-evaluated on every rebuild (choosing rebuilds; cancelling the
    /// prompt leaves the choice saved, so the note stays).
    /// </summary>
    private IEnumerable<SettingsRowSpec> PendingLanguageNote()
    {
        if (!LanguageChangePending(_ctx.Settings)) yield break;
        yield return new LabelSpec(() => StringTable.Current.T("language-recovery.note.pending")) { Simple = true };
    }

    /// <summary>
    /// Whether the SAVED language would differ from the one this process runs. "Pending" is a
    /// resolution question, not a selection one: picking "System" on a de-DE machine while German
    /// runs is not a change, and picking it while English runs is. The process language is the
    /// table's — loaded once at startup, never switched live.
    /// </summary>
    private static bool LanguageChangePending(EyeCtrlSettings settings) =>
        StringTable.ResolveLanguage(settings.General.UiLanguage, StringTable.Current.Culture)
        != StringTable.Current.Language;

    /// <summary>
    /// Saves a language choice, shows the pending note, and opens the restart prompt in the TARGET
    /// language. The choice resolves to the language already running = nothing to apply, so no
    /// prompt (and no note) is shown.
    /// </summary>
    /// <param name="code">The combo's code, a <see cref="UiLanguageTokens"/> JSON token.</param>
    private void OnLanguageChosen(string code)
    {
        if (!UiLanguageTokens.TryFromJson(code, out var language)) return;

        var settings = _ctx.Settings;
        settings.General.UiLanguage = language;
        _ctx.ApplyAndSave();
        _ctx.RebuildContent();

        if (!LanguageChangePending(settings)) return;

        // The named-language lookup is gated by the manifest: until T11 a German target still
        // shows English, so Step 0 shows no German. F2: resolve the TARGET first — "System"
        // means "whatever System resolves to on this machine" (the ticket), while the lookup's
        // own System means "this instance's language"; passing a raw System would render the
        // prompt in the RUNNING language.
        var table = StringTable.Current;
        var target = StringTable.ResolveLanguage(language, table.Culture);
        _ctx.ViewModel.ShowModal(
            table.T(target, "language-recovery.prompt.message"),
            [
                new ModalChoice("language_restart",
                    table.T(target, "language-recovery.prompt.restart"),
                    ModalChoiceKind.Affirmative,
                    () => _ctx.Messenger.Send(new RestartAppRequestedMessage())),
                new ModalChoice("language_cancel",
                    table.T(target, "language-recovery.prompt.cancel"),
                    ModalChoiceKind.Negative, null),
            ]);
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
            yield return new LabelSpec(
                () => StringTable.Current.T("settings.general.supervision.unavailable")) { Simple = true };
            yield break;
        }

        yield return new ToggleSpec("g_supervision",
            StringTable.Current.T("settings.general.supervision.label"),
            () => supervision.Enabled,
            v =>
            {
                if (v) supervision.RequestEnable();
                else supervision.RequestDisable();
            })
            { Description = StringTable.Current.T("settings.general.supervision.description"), Simple = true };

        // Ticket 07: enabled but no Supervisor answering (e.g. after a reboot, before the startup
        // ensure lands, or after a Supervisor exit) — offer the retry, sized like the Restart
        // button for aimability, and keep the toggle On (the row never flips the disk flag itself).
        if (supervision.Enabled && !supervision.IsSupervisorConnected)
        {
            yield return new ButtonSpec("g_supervision_retry",
                StringTable.Current.T("settings.general.supervision_retry.label"),
                () => supervision.RequestEnsure(), ButtonKind.Default, Width: 300, Height: 100) { Simple = true };
        }

        yield return new LabelSpec(() => supervision.StatusText) { Simple = true };
    }

    private void OnRestart()
    {
        // Only offer what is installed and not already running. The multi-choice modal is built
        // directly on the ViewModel (the same object ShowModal's two-button helper wraps).
        if (_ctx.Restart.ShouldOfferSupervisor())
        {
            _ctx.ViewModel.ShowModal(
                StringTable.Current.T("settings.general.restart.supervised.message"),
                [
                    new ModalChoice("restart_supervised",
                        StringTable.Current.T("settings.general.restart.supervised.option"),
                        ModalChoiceKind.Affirmative,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true))),
                    new ModalChoice("restart_plain",
                        StringTable.Current.T("settings.general.restart.plain.option"),
                        ModalChoiceKind.Neutral,
                        () => _ctx.Messenger.Send(new RestartAppRequestedMessage())),
                    new ModalChoice("restart_cancel",
                        StringTable.Current.T("settings.general.restart.cancel.option"),
                        ModalChoiceKind.Negative, null),
                ]);
        }
        else
        {
            _ctx.ShowModal(StringTable.Current.T("settings.general.restart.message"),
                () => _ctx.Messenger.Send(new RestartAppRequestedMessage()));
        }
    }
}
