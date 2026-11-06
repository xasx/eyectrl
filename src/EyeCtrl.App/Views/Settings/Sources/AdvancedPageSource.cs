namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;

internal sealed class AdvancedPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public AdvancedPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var st = _ctx.Settings;
        var backdrop = st.Backdrop;
        var drag = st.Drag;
        var scroll = st.Scroll;
        var ext = st.ExternalDwell;
        var repeat = st.Repeat;
        return
        [
            new PageSpec([
                // Backdrop
                new ToggleSpec("av_backdrop_enabled",
                    StringTable.Current.T("settings.advanced.backdrop_enabled.label"),
                    () => backdrop.Enabled, v => { backdrop.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_enabled.description") },
                new ComboBoxSpec("av_backdrop_type",
                    StringTable.Current.T("settings.advanced.backdrop_type.label"),
                    [
                        StringTable.Current.T("settings.advanced.backdrop_type.option.glass"),
                        StringTable.Current.T("settings.advanced.backdrop_type.option.solid_darken"),
                    ],
                    Enum.GetNames<Core.BackdropType>(),
                    () => backdrop.Type.ToString(),
                    v => { backdrop.Type = Enum.Parse<Core.BackdropType>(v); _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_type.description") },
                new SliderSpec("av_backdrop_opacity",
                    StringTable.Current.T("settings.advanced.backdrop_opacity.label"),
                    Core.Settings.BackdropSettings.OpacityMin, Core.Settings.BackdropSettings.OpacityMax,
                    0.05, 2,
                    () => backdrop.Opacity, v => { backdrop.Opacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_opacity.description") },
                new SliderSpec("av_backdrop_hide",
                    StringTable.Current.T("settings.advanced.backdrop_hide.label"), 200, 2000, 50, 0,
                    () => st.Dwell.BackdropHideButtonDwellMs, v => { st.Dwell.BackdropHideButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.backdrop_hide.description") },
                new SeparatorSpec(),
                // Drag
                new SliderSpec("av_drag_settle",
                    StringTable.Current.T("settings.advanced.drag_settle.label"), 200, 2000, 50, 0,
                    () => drag.SettleDwellMs, v => { drag.SettleDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_settle.description") },
                new SliderSpec("av_drag_release",
                    StringTable.Current.T("settings.advanced.drag_release.label"), 200, 2000, 50, 0,
                    () => st.Dwell.DragReleaseButtonDwellMs, v => { st.Dwell.DragReleaseButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_release.description") },
                new SliderSpec("av_drag_right_edge",
                    StringTable.Current.T("settings.advanced.drag_right_edge.label"), 200, 2000, 50, 0,
                    () => st.Dwell.DragRightEdgeButtonDwellMs, v => { st.Dwell.DragRightEdgeButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_right_edge.description") },
                new SliderSpec("av_drag_radius",
                    StringTable.Current.T("settings.advanced.drag_radius.label"), 10, 200, 5, 0,
                    () => drag.SettleRadiusPixels, v => { drag.SettleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_radius.description") },
                new ColorPickerSpec("av_drag_color",
                    StringTable.Current.T("settings.advanced.drag_color.label"),
                    () => drag.TrailColor, v => { drag.TrailColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = StringTable.Current.T("settings.advanced.drag_color.description") },
                new SliderSpec("av_drag_opacity",
                    StringTable.Current.T("settings.advanced.drag_opacity.label"), 0.1, 1.0, 0.05, 2,
                    () => drag.TrailOpacity, v => { drag.TrailOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.drag_opacity.description") },
                new SeparatorSpec(),
                // Scroll
                new SliderSpec("av_scroll_sens",
                    StringTable.Current.T("settings.advanced.scroll_sensitivity.label"), 0.5, 2.0, 0.1, 1,
                    () => scroll.Sensitivity, v => { scroll.Sensitivity = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.scroll_sensitivity.description") },
                new SliderSpec("av_scroll_stop",
                    StringTable.Current.T("settings.advanced.scroll_stop.label"), 200, 2000, 50, 0,
                    () => st.Dwell.ScrollStopButtonDwellMs, v => { st.Dwell.ScrollStopButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.scroll_stop.description") },
                new SeparatorSpec(),
                // Repeat
                new SliderSpec("av_repeat_dwell",
                    StringTable.Current.T("settings.advanced.repeat_dwell.label"), 200, 2000, 50, 0,
                    () => repeat.DwellMs, v => { repeat.DwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.repeat_dwell.description") },
                new SliderSpec("av_repeat_stop",
                    StringTable.Current.T("settings.advanced.repeat_stop.label"), 200, 2000, 50, 0,
                    () => repeat.StopButtonDwellMs, v => { repeat.StopButtonDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.repeat_stop.description") },
                new SeparatorSpec(),
                // External Dwell
                new ToggleSpec("av_ext_enabled",
                    StringTable.Current.T("settings.advanced.external_dwell.label"),
                    () => ext.Enabled, v => { ext.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_dwell.description") },
                new SliderSpec("av_ext_dwell",
                    StringTable.Current.T("settings.advanced.external_hold.label"), 200, 3000, 50, 0,
                    () => ext.DefaultDwellMs, v => { ext.DefaultDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_hold.description") },
                new SliderSpec("av_ext_cooldown",
                    StringTable.Current.T("settings.advanced.external_cooldown.label"), 0, 5000, 100, 0,
                    () => ext.CooldownMs, v => { ext.CooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.advanced.external_cooldown.description") },
                new ButtonSpec("av_reset",
                    StringTable.Current.T("settings.advanced.reset.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.advanced.reset.message"), () =>
                    {
                        var b = new Core.Settings.BackdropSettings();
                        var d = new Core.Settings.DragSettings();
                        var dw = new Core.Settings.DwellSettings();
                        var sc = new Core.Settings.ScrollSettings();
                        var e = new Core.Settings.ExternalDwellSettings();
                        var r = new Core.Settings.RepeatSettings();
                        st.Backdrop.Enabled = b.Enabled;
                        st.Backdrop.Type = b.Type;
                        st.Backdrop.Opacity = b.Opacity;
                        st.Dwell.BackdropHideButtonDwellMs = dw.BackdropHideButtonDwellMs;
                        st.Drag.SettleDwellMs = d.SettleDwellMs;
                        st.Dwell.DragReleaseButtonDwellMs = dw.DragReleaseButtonDwellMs;
                        st.Dwell.DragRightEdgeButtonDwellMs = dw.DragRightEdgeButtonDwellMs;
                        st.Drag.SettleRadiusPixels = d.SettleRadiusPixels;
                        st.Drag.TrailColor = d.TrailColor;
                        st.Drag.TrailOpacity = d.TrailOpacity;
                        st.Scroll.Sensitivity = sc.Sensitivity;
                        st.Dwell.ScrollStopButtonDwellMs = dw.ScrollStopButtonDwellMs;
                        st.Repeat.DwellMs = r.DwellMs;
                        st.Repeat.StopButtonDwellMs = r.StopButtonDwellMs;
                        st.ExternalDwell.Enabled = e.Enabled;
                        st.ExternalDwell.DefaultDwellMs = e.DefaultDwellMs;
                        st.ExternalDwell.CooldownMs = e.CooldownMs;
                        // TrailColor's default is a Midnight absolute; re-derive for the live
                        // theme as startup does (review round 2, N2 — resets must not reopen
                        // the cross-theme ring/trail carry-over at runtime).
                        EyeCtrl.App.Services.ThemeStartup.MigrateHaloedMarks(
                            EyeCtrl.Controls.Theme.Tokens.Current, st);
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
                ..BuildSetupRows(),
            ]),
        ];
    }

    /// <summary>
    /// C9 S6f's three „Erweitert" rows, at the very end of the page and in „Alle Optionen" only
    /// (<see cref="SettingsRowSpec.Simple" /> stays false on every spec). A null gateways service
    /// — headless test contexts, a host without the App composition — builds no setup rows at all.
    /// <para>
    /// Not installed (a dev build, or not running from Program Files\EyeCtrl) hides all three:
    /// the ticket's chosen option, because for a gaze user a dead row is worse than the row's
    /// absence — three labels that dwell into nothing read as a broken screen.
    /// </para>
    /// <para>
    /// Review 01 N2: while the catalog verification runs on its worker thread, a same-height
    /// placeholder stands in for the update row, so the rows never shift under the gaze when the
    /// result lands (the window starts the probe when it opens; this build call is the idempotent
    /// fallback for a host that did not).
    /// </para>
    /// <para>
    /// Review 01 M4: the policy gate covers the rollback row too, exactly like the update row —
    /// plan §10 wants both blocked when Windows would prompt. One blocked line covers both rows
    /// (the same text twice would read as a duplicated screen), and nothing dwellable is offered.
    /// </para>
    /// </summary>
    private IEnumerable<SettingsRowSpec> BuildSetupRows()
    {
        if (_ctx.SetupGateways is not { Installed: true } gate)
            yield break;

        yield return new SeparatorSpec();

        var blocked = gate.PolicyBlocks;
        var blockedLineEmitted = false;

        var offer = gate.UpdateOffer;
        if (offer is null && gate.UpdateOfferPending)
        {
            // Review 02 R4: plain, non-interactive text — no dwellable no-op button — at roughly
            // the described button row's footprint, so the rows below do not shift when the
            // result lands (and on no other tab would it).
            yield return new LabelSpec(
                () => StringTable.Current.T("settings.advanced.update.checking"),
                Wrap: false,
                MinHeight: SettingsRowSpec.DescribedButtonRowHeight);
        }
        else if (offer is not null)
        {
            if (blocked)
            {
                yield return BlockedLine();
                blockedLineEmitted = true;
            }
            else
            {
                yield return new ButtonSpec("av_update",
                    StringTable.Current.T("settings.advanced.update.label"),
                    () => ConfirmUpdate(gate, offer), ButtonKind.Default, Width: 380, Height: 100)
                {
                    Description = StringTable.Current.T(
                        "settings.advanced.update.description", offer.Version, offer.InstalledVersion),
                };
            }
        }

        if (gate.PreviousAvailable)
        {
            if (blocked)
            {
                if (!blockedLineEmitted)
                    yield return BlockedLine();
            }
            else
            {
                yield return new ButtonSpec("av_rollback",
                    StringTable.Current.T("settings.advanced.rollback.label"),
                    () => ConfirmRollback(gate), ButtonKind.Default, Width: 380, Height: 100)
                {
                    Description = StringTable.Current.T(
                        "settings.advanced.rollback.description", gate.PreviousVersion ?? ""),
                };
            }
        }

        yield return new ButtonSpec("av_uninstall",
            StringTable.Current.T("settings.advanced.uninstall.label"),
            () =>
            {
                // fixup-06 M2: a guard refusal means Setup is already starting or running — its
                // own window is the answer. Only a launch that failed shows the failure line.
                // fixup-08: the presenter hides Settings on Started and brings it back by itself
                // on every end that does not uninstall; a host without one keeps today's call.
                var start = _ctx.UninstallHandoff is { } handoff
                    ? handoff.Start(gate)
                    : gate.StartUninstallHandoff();
                if (start == SetupHandoffStart.Failed)
                    _ctx.ShowError(StringTable.Current.T("settings.advanced.setup.startFailed"));
            },
            ButtonKind.Danger, Width: 380, Height: 100)
        {
            Description = StringTable.Current.T("settings.advanced.uninstall.description"),
        };
    }

    /// <summary>The blocked policy line — shown and disabled, because there is nothing to dwell.</summary>
    private static LabelSpec BlockedLine() =>
        new(() => StringTable.Current.T("settings.advanced.update.blocked"));

    /// <summary>The update confirm: the ticket's „Jetzt installieren" / „Später" pair, in the
    /// existing modal (the „Allgemein zurücksetzen" pattern) with custom labels. The detail line
    /// rides in the modal body under the question, like every prose confirm in the window.</summary>
    private void ConfirmUpdate(ISetupGateways gate, SetupUpdateOffer offer)
    {
        var title = StringTable.Current.T("update.confirm.title", offer.Version);
        var detail = StringTable.Current.T("update.confirm.detail");
        _ctx.ViewModel.ShowModal(
            title + "\n\n" + detail,
            [
                new ModalChoice("setup_update_install",
                    StringTable.Current.T("update.confirm.install"),
                    ModalChoiceKind.Affirmative,
                    () => RunUpdate(gate, offer)),
                new ModalChoice("setup_update_later",
                    StringTable.Current.T("update.confirm.later"),
                    ModalChoiceKind.Negative,
                    null),
            ]);
    }

    /// <summary>The rollback confirm: „Zurückwechseln" / „Später", same modal pattern.</summary>
    private void ConfirmRollback(ISetupGateways gate)
    {
        _ctx.ViewModel.ShowModal(
            StringTable.Current.T("rollback.confirm.title", gate.PreviousVersion ?? ""),
            [
                new ModalChoice("setup_rollback_go",
                    StringTable.Current.T("rollback.confirm.go"),
                    ModalChoiceKind.Affirmative,
                    () => RunRollback(gate)),
                new ModalChoice("setup_rollback_later",
                    StringTable.Current.T("rollback.confirm.later"),
                    ModalChoiceKind.Negative,
                    null),
            ]);
    }

    /// <summary>Review 01 N3: a refused or throwing hand-off keeps EyeCtrl running and says so —
    /// after a completed dwell, silence reads as the button not working. fixup-06 M2: only a
    /// FAILED launch says so; a duplicate activation while Setup is already starting is silent.</summary>
    private void RunUpdate(ISetupGateways gate, SetupUpdateOffer offer)
    {
        if (gate.StartUpdateHandoff(offer) == SetupHandoffStart.Failed)
            _ctx.ShowError(StringTable.Current.T("settings.advanced.setup.startFailed"));
    }

    /// <summary>The rollback hand-off with the same failure line.</summary>
    private void RunRollback(ISetupGateways gate)
    {
        if (gate.StartRollbackHandoff() == SetupHandoffStart.Failed)
            _ctx.ShowError(StringTable.Current.T("settings.advanced.setup.startFailed"));
    }
}
