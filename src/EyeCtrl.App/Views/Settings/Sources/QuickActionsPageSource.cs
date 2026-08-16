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
    private const int QaActionsPerPage = 4;
    private const int QaPresetsPerPage = 6;

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

    // internal (not private) so the page-source test can drive the state machine directly instead of
    // reflecting into it (reflection on an enum-typed arg throws ArgumentException — Int32 does not
    // widen to a boxed enum). EyeCtrl.App.Tests is InternalsVisibleTo from EyeCtrl.App.
    internal void GoTo(QaView v)
    {
        _view = v;
        _ctx.RebuildContent();
    }

    public IReadOnlyList<PageSpec> Build() => _view switch
    {
        QaView.PickType => TypePicker(),
        QaView.PickShortcut => PresetPicker("qa_ks", Core.Models.QuickActionPresets.KeyboardShortcuts, _ctx.ViewModel.QuickActionsEditor.AddKeyboardShortcut),
        QaView.PickMedia => PresetPicker("qa_md", Core.Models.QuickActionPresets.MediaControls, _ctx.ViewModel.QuickActionsEditor.AddMediaControl),
        QaView.PickWindowAction => PresetPicker("qa_win", Core.Models.QuickActionPresets.WindowActions, _ctx.ViewModel.QuickActionsEditor.AddWindowAction),
        QaView.PickApp => AppPicker(),
        _ => List(),
    };

    private IReadOnlyList<PageSpec> List()
    {
        var qa = _ctx.Settings.QuickActions;
        var editor = _ctx.ViewModel.QuickActionsEditor;
        var pages = new List<PageSpec>();

        pages.Add(new PageSpec([
            new ToggleSpec("qa_enabled", "Enable Quick Actions", () => qa.Enabled, v => { qa.Enabled = v; _ctx.ApplyAndSave(); }),
            new ComboBoxSpec("qa_edge", "Summon Edge", new[] { "Left", "Right", "Top", "Bottom" }, null, () => qa.SummonEdge, v => { qa.SummonEdge = v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_summon", "Summon Threshold (ms)", 200, 2000, 50, 0, () => qa.SummonThresholdMs, v => { qa.SummonThresholdMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_dismiss", "Dismiss Timeout (ms)", 1000, 15000, 500, 0, () => qa.DismissTimeoutMs, v => { qa.DismissTimeoutMs = (int)v; _ctx.ApplyAndSave(); }),
            new SliderSpec("qa_columns", "Grid Columns", 2, 5, 1, 0, () => qa.GridColumns, v => { qa.GridColumns = (int)v; _ctx.ApplyAndSave(); }),
            new ButtonSpec("qa_add", "\uFF0B  Add action", () => GoTo(QaView.PickType), ButtonKind.Default, Width: 360, Height: 100),
        ]));

        var actions = editor.Actions.ToList();
        for (int i = 0; i < actions.Count; i += QaActionsPerPage)
        {
            var chunk = actions.Skip(i).Take(QaActionsPerPage).ToList();
            var rows = new List<SettingsRowSpec>();
            foreach (var a in chunk)
            {
                var captured = a;
                var caption = $"{Glyph(a.Type)}  {a.Label}";
                rows.Add(new ToggleSpec("qa_en_" + a.Id, caption, () => captured.Enabled, _ => editor.ToggleEnabled(captured.Id)));
                rows.Add(new ButtonSpec("qa_del_" + a.Id, "\u2715  Delete",
                    () => _ctx.ShowModal($"Delete \"{captured.Label}\"?", () => { editor.DeleteAction(captured.Id); _ctx.RebuildContent(); }),
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

    private IReadOnlyList<PageSpec> PresetPicker(string idPrefix,
        IReadOnlyList<(string Label, string Payload)> presets, Action<string, string> add) =>
        ChunkPresets(presets.Count, idPrefix, i =>
        {
            var (label, payload) = presets[i];
            return (label, () => { add(label, payload); GoTo(QaView.List); });
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
