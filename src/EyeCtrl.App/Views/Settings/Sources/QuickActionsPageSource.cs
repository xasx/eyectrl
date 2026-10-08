namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.Models;

/// <summary>
/// The Quick Actions tab. Owns the <see cref="QaView"/> sub-view state machine (list + four
/// pickers), the per-page back-button id scheme, and pagination via <see cref="ChunkPresets"/>.
/// <see cref="ResetView"/> is called by the shell on every tab switch so re-entering QA lands on
/// the list view rather than wherever the user left it.
/// </summary>
internal sealed class QuickActionsPageSource : ISettingsPageSource
{
    /// <summary>
    /// How many configured quick actions one List page holds. Bounded by the CONTENT HEIGHT
    /// BUDGET, not by taste — and note the factor of two.
    ///
    /// <para>
    /// Each action emits TWO rows, not one: an enable toggle (<c>qa_en_{id}</c>) and a Delete
    /// button (<c>qa_del_{id}</c>). <c>SettingsWindow.CreateControl</c> centres every settings-row
    /// button in a <c>LabelledRowMinHeight</c> (150 DIP) box, so an action costs 300 DIP and the
    /// n-th action's two circles land at 75 + 300n and 225 + 300n. The content cell on the
    /// 1920x1080 display this application is used on is 688 DIP, so it holds four rows — i.e.
    /// TWO actions: circles at 75, 225, 375, 525, with 88 DIP to spare.
    /// </para>
    /// <para>
    /// This was 4, which put EIGHT rows on a four-row page: the third action's Delete landed at
    /// 825, the fourth's toggle at 975 and its Delete at 1125, all outside the cell. The content
    /// area has NO ScrollViewer (SettingsWindow.xaml: "no ScrollViewer — paginated"), so those
    /// were registered dwell targets over pixels the user could not look at — and one of them was
    /// DELETE, a destructive control a gaze-only user could neither aim at nor avoid. Pre-existing
    /// rather than a restyle regression: on main @ dd1baf5c the same page measured 992.00 against
    /// a 796.00 budget. Issue #113.
    /// </para>
    /// <para>
    /// 3 is not the answer: six rows is 900 DIP and puts the third action's pair at 675 and 825,
    /// so its Delete is below the fold again. The two-rows-per-action factor is what forces an
    /// even number here, and halving this to 2 is what the extra page-turn per pair buys.
    /// </para>
    /// <para>
    /// **Nothing re-paginates these pages by height** — see <see cref="QaPresetsPerPage"/> for why
    /// <see cref="PrefersAutoPagination"/> is false here. This constant IS the pagination, and it
    /// is gated by
    /// <c>HandChunkedPageFitTests.EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell</c>
    /// over a fixture that seeds enough actions to build both a full page and a partial one.
    /// </para>
    /// </summary>
    private const int QaActionsPerPage = 2;

    /// <summary>
    /// How many of the List view's own configuration rows go on one page. Bounded by the CONTENT
    /// HEIGHT BUDGET, not by taste.
    ///
    /// <para>
    /// The tab has six config rows at the uniform 150 DIP pitch — 900 DIP — against a 688 DIP
    /// content cell that holds FOUR (circles at 75, 225, 375, 525, with 88 DIP to spare). Six into
    /// four does not go, so unlike <see cref="QaActionsPerPage"/> and
    /// <see cref="QaPresetsPerPage"/> this was never fixable by lowering a number alone: a row had
    /// to move to a second page. Before that, <c>qa_add</c> sat at 825 — 137 DIP below the fold,
    /// with no ScrollViewer to bring it back — so the tab's PRIMARY ACTION was a registered dwell
    /// target the user could not look at. Pre-existing, not a restyle regression: on
    /// main @ dd1baf5c that page measured 1019.70 against a 796.00 budget and put the same circle
    /// at 945.70, 149.70 DIP below the fold. Issue #113.
    /// </para>
    /// <para>
    /// **Add action is emitted FIRST, and the order is part of the fix.** Chunking alone would
    /// have pushed <c>qa_add</c> onto page 2, so every "add an action" would open with a dwell
    /// page-turn — while the rows it displaced, Dismiss Timeout and Grid Columns, are set once and
    /// left alone. Leading with Add puts its circle at 75 and spends the page-turn on the rows
    /// that do not need it. Chosen by the user, who navigates this tab by gaze; the alternative
    /// (original order, Add on page 2) was rendered and rejected.
    /// </para>
    /// <para>
    /// Gated, like its two siblings, by
    /// <c>HandChunkedPageFitTests.EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell</c>
    /// (reachability) and by
    /// <c>HandChunkedPageFitTests.EveryChunkedQuickActionsPage_KeepsHalfARowOfTrailingSlack</c>
    /// (the 88-DIP slack this comment advertises — reachability alone would tolerate a fifth
    /// row's 13) — nothing re-paginates these pages by height, so this constant IS the pagination.
    /// </para>
    /// </summary>
    private const int QaConfigRowsPerPage = 4;

    /// <summary>
    /// How many presets one picker page holds. Bounded by the CONTENT HEIGHT BUDGET, not by taste.
    ///
    /// <para>
    /// A preset page is a 70 DIP Back row plus this many 100 DIP preset buttons, and
    /// <c>SettingsWindow.CreateControl</c> centres every settings-row button in a
    /// <c>LabelledRowMinHeight</c> (150 DIP) box — so each row costs a uniform 150 DIP and the
    /// n-th dwell circle's centre lands at 75 + 150n. The content cell on the 1920x1080 display
    /// this application is used on is 688 DIP (measured: window 972, less the 124 DIP page-header
    /// block, ContentArea's 32, ContentCard's 16 and the 112 DIP pagination reserve), so it holds
    /// FOUR rows: circles at 75, 225, 375, 525, with 88 DIP to spare.
    /// </para>
    /// <para>
    /// This was 6 and had to come down to 3. The two-column restyle made the row pitch uniform at
    /// 150 and added the page header, which cut the budget from 796 to 688 while a 6-preset page
    /// stayed 1050 DIP tall — putting the last two circles at 825 and 975, outside the cell. The
    /// content area has NO ScrollViewer (SettingsWindow.xaml: "no ScrollViewer — paginated"), so
    /// those were registered dwell targets over pixels the user could not look at: for a
    /// gaze-only user, a setting that had silently ceased to exist (issue #113).
    /// </para>
    /// <para>
    /// 4 is NOT the answer even though its five circles all technically fit: the last one lands at
    /// 675 against 688, and 13 DIP is not a margin to rely on — 62 DIP of the page's trailing
    /// margin is already being clipped at that point. 3 leaves 88.
    /// </para>
    /// <para>
    /// **Nothing re-paginates these pages by height.** <see cref="PrefersAutoPagination"/> is
    /// false here (deliberately — the page boundaries carry navigation semantics, the per-page
    /// <c>qa_back_{page}</c> ids), so <c>SettingsWindow.MaybeRepaginateByHeight</c> passes
    /// <see cref="ChunkPresets"/>'s pages straight through and the height budget is never
    /// consulted. This constant IS the pagination. Gated by
    /// <c>HandChunkedPageFitTests.EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell</c>,
    /// which arranges every page this source emits, in every <see cref="QaView"/>, and asserts
    /// each dwell circle's centre lies inside that budget.
    /// </para>
    /// </summary>
    private const int QaPresetsPerPage = 3;

    // internal so the test can reference QuickActionsPageSource.QaView.PickShortcut when calling GoTo.
    internal enum QaView { List, PickType, PickShortcut, PickMedia, PickApp, PickWindowAction }

    private readonly SettingsPageContext _ctx;
    private QaView _view = QaView.List;

    public QuickActionsPageSource(SettingsPageContext ctx) => _ctx = ctx;

    /// <summary>Called by the shell when the user switches tabs, so re-entering QA lands on the list.</summary>
    public void ResetView()
    {
        _view = QaView.List;
    }

    /// <summary>
    /// The six views the state machine can show, in enum order. The reachability gate drives
    /// every one, because the preset pages — the ones issue #113 was about — exist ONLY inside
    /// the picker views: the List view alone never builds a <c>ChunkPresets</c> page, so a gate
    /// that measured only the landing view would miss the bug entirely.
    /// </summary>
    public IReadOnlyList<(string Name, Action Enter)> Views =>
        Enum.GetValues<QaView>().Select(v => (v.ToString(), (Action)(() => GoTo(v)))).ToList();

    // internal (not private) so the page-source test can drive the state machine directly instead of
    // reflecting into it (reflection on an enum-typed arg throws ArgumentException — Int32 does not
    // widen to a boxed enum). EyeCtrl.App.Tests is InternalsVisibleTo from EyeCtrl.App.
    internal void GoTo(QaView v)
    {
        _view = v;
        // Every view's page 0 is its landing page. A page index carried over from the view we
        // left lands the user mid-view: returning from a multi-page picker used to open the
        // config tail ({Dismiss Timeout, Grid Columns}) instead of the qa_add page, because
        // the picker's index survived into the List's re-chunked pages. ShowTab resets the
        // page for the same reason when switching tabs.
        _ctx.ViewModel.Settings.CurrentPage = 0;
        _ctx.RebuildContent();
    }

    public IReadOnlyList<PageSpec> Build() => _view switch
    {
        QaView.PickType => TypePicker(),
        QaView.PickShortcut => PresetPicker("qa_ks", Core.Models.QuickActionPresets.KeyboardShortcuts,
            p => _ctx.ViewModel.QuickActionsEditor.AddKeyboardShortcut(p.Label, p.Payload, p.Id)),
        QaView.PickMedia => PresetPicker("qa_md", Core.Models.QuickActionPresets.MediaControls,
            p => _ctx.ViewModel.QuickActionsEditor.AddMediaControl(p.Label, p.Payload, p.Id)),
        QaView.PickWindowAction => PresetPicker("qa_win", Core.Models.QuickActionPresets.WindowActions,
            p => _ctx.ViewModel.QuickActionsEditor.AddWindowAction(p.Label, p.Payload, p.Id)),
        QaView.PickApp => AppPicker(),
        _ => List(),
    };

    private IReadOnlyList<PageSpec> List()
    {
        var qa = _ctx.Settings.QuickActions;
        var editor = _ctx.ViewModel.QuickActionsEditor;
        var pages = new List<PageSpec>();

        // Add action leads, and that ORDER IS LOAD-BEARING — see QaConfigRowsPerPage.
        var config = new List<SettingsRowSpec>
        {
            new ButtonSpec("qa_add", "\uFF0B  Add action", () => GoTo(QaView.PickType), ButtonKind.Default, Width: 360, Height: 100),
            new ToggleSpec("qa_enabled", "Enable Quick Actions", () => qa.Enabled, v => { qa.Enabled = v; _ctx.ApplyAndSave(); }),
            new ComboBoxSpec("qa_edge", "Summon Edge",
                new[] { "Left", "Right", "Top", "Bottom" }, new[] { "Left", "Right", "Top", "Bottom" },
                () => qa.SummonEdge, v => { qa.SummonEdge = v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_summon", "Summon Threshold (ms)", 200, 2000, 50, 0, () => qa.SummonThresholdMs, v => { qa.SummonThresholdMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_dismiss", "Dismiss Timeout (ms)", 1000, 15000, 500, 0, () => qa.DismissTimeoutMs, v => { qa.DismissTimeoutMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_columns", "Grid Columns", 2, 5, 1, 0, () => qa.GridColumns, v => { qa.GridColumns = (int)v; _ctx.ApplyAndSave(); }),
        };

        for (int i = 0; i < config.Count; i += QaConfigRowsPerPage)
            pages.Add(new PageSpec(config.Skip(i).Take(QaConfigRowsPerPage).ToList()));

        var actions = editor.Actions.ToList();
        for (int i = 0; i < actions.Count; i += QaActionsPerPage)
        {
            var chunk = actions.Skip(i).Take(QaActionsPerPage).ToList();
            var rows = new List<SettingsRowSpec>();
            foreach (var a in chunk)
            {
                var captured = a;
                var caption = $"{Glyph(a.Type)}  {QuickActionPresets.DisplayLabel(a, PresetLabelFor)}";
                rows.Add(new ToggleSpec("qa_en_" + a.Id, caption, () => captured.Enabled, _ => editor.ToggleEnabled(captured.Id)));
                rows.Add(new ButtonSpec("qa_del_" + a.Id, "\u2715  Delete",
                    () => _ctx.ShowModal($"Delete \"{QuickActionPresets.DisplayLabel(captured, PresetLabelFor)}\"?", () => { editor.DeleteAction(captured.Id); _ctx.RebuildContent(); }),
                    ButtonKind.Delete, Width: 360, Height: 70));
            }
            pages.Add(new PageSpec(rows));
        }
        return pages;
    }

    private IReadOnlyList<PageSpec> TypePicker() =>
    [
        new PageSpec([
            .. BackRow(0),
            new ButtonSpec("qa_type_kbd", "\u2328  Keyboard Shortcut", () => GoTo(QaView.PickShortcut), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_media", "\u266B  Media Control", () => GoTo(QaView.PickMedia), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_app", "\u25A6  Launch App", () => GoTo(QaView.PickApp), ButtonKind.Default, Width: 420, Height: 90),
            new ButtonSpec("qa_type_window", "\u25A3  Window Action", () => GoTo(QaView.PickWindowAction), ButtonKind.Default, Width: 420, Height: 90),
        ]),
    ];

    /// <summary>
    /// The Settings surface's preset-label function. Every preset label the tab shows — the
    /// picker buttons, the list caption, the delete modal — resolves through this one function,
    /// so keying the tab later means changing its body, not its call sites.
    /// </summary>
    private static string? PresetLabelFor(string presetId) => QuickActionPresets.EnglishLabelFor(presetId);

    private IReadOnlyList<PageSpec> PresetPicker(string idPrefix,
        IReadOnlyList<QuickActionPreset> presets, Action<QuickActionPreset> add) =>
        ChunkPresets(presets.Count, idPrefix, i =>
        {
            var preset = presets[i];
            // The button text comes from the host's label function; the stored English label is
            // written only when the tile is created (the Add* call above).
            return (QuickActionPresets.DisplayLabel(preset, PresetLabelFor),
                    () => { add(preset); GoTo(QaView.List); });
        });

    private IReadOnlyList<PageSpec> AppPicker()
    {
        var editor = _ctx.ViewModel.QuickActionsEditor;
        var apps = editor.InstalledApps;
        if (apps.Count == 0)
        {
            return
            [
                new PageSpec([
                    new LabelSpec(() => "No installed apps found."),
                    BackButton(0),
                ]),
            ];
        }
        return ChunkPresets(apps.Count, "qa_app", i =>
        {
            var app = apps[i];
            return (app.DisplayName, () => { editor.AddApp(app); GoTo(QaView.List); });
        });
    }

    private IReadOnlyList<PageSpec> ChunkPresets(int count, string idPrefix, Func<int, (string Label, Action OnPick)> itemAt)
    {
        var pages = new List<PageSpec>();
        for (int start = 0, page = 0; start < count; start += QaPresetsPerPage, page++)
        {
            // qa_back_{page}: preserve the original per-page back-button id (QaBackButton(page)).
            var rows = BackRow(page);
            for (int i = start; i < Math.Min(start + QaPresetsPerPage, count); i++)
            {
                var (label, onPick) = itemAt(i);
                var id = $"{idPrefix}_{i}";
                rows.Add(new ButtonSpec(id, label, onPick, ButtonKind.Default,
                    Width: SettingsWindow.PickerButtonWidth, Height: 100));
            }
            pages.Add(new PageSpec(rows));
        }
        return pages;
    }

    // Instance (not static): calls the instance BackButton, which captures `this` for GoTo.
    private List<SettingsRowSpec> BackRow(int page) => new() { BackButton(page) };
    private ButtonSpec BackButton(int page) => new("qa_back_" + page, "\u2190  Back",
        () => GoTo(QaView.List), ButtonKind.Default, Width: 360, Height: 70);

    private static string Glyph(QuickActionType t) => t switch
    {
        QuickActionType.KeyboardShortcut => "\u2328",
        QuickActionType.CannedText => "T",
        QuickActionType.LaunchExe => "\u229E",
        QuickActionType.LaunchWindowsApp => "\u25A6",
        QuickActionType.LaunchUrl => "@",
        QuickActionType.MediaControl => "\u266B",
        QuickActionType.WindowAction => "\u25A3",
        _ => "\u26A1",
    };
}
