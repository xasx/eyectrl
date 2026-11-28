namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Wave D D4: every dwell or look-time row lives on one tab („Verweilzeiten"), grouped by where
/// the owner meets it. The eight rows that used to sit on „Erweitert", the restart notice on
/// „Allgemein" and the break banner on the eye-care page moved here; each row's description names
/// every surface it moves, including the couplings that were previously invisible (the toolbar
/// row also sets three ✕ discs, and the manual-selection action-menu row also sets the Lupe's own
/// dwell). This is the source-level half of the sitting's two find tasks.
/// </summary>
public class LookTimeRowsOnTheDwellPageTests
{
    /// <summary>
    /// Every row whose value is a look time — the audit's 19 plus the timers that already lived on
    /// the Dwell tab. A row that leaves this list must be a deliberate product decision.
    /// </summary>
    private static readonly string[] LookTimeRowIds =
    [
        // Activation, menus and the magnifier
        "d_act", "d_act_show", "d_menu", "d_ManualSelection", "d_manual_side",
        // Bars and windows
        "d_tool", "g_restart_notice", "av_backdrop_hide", "d_ts_tile",
        // Typing
        "d_key", "d_pred",
        // Detection and timing
        "d_fix", "d_rad", "d_cooldown",
        // Drag, scroll and repeat
        "av_drag_settle", "av_drag_release", "av_drag_right_edge", "av_scroll_stop",
        "av_repeat_dwell", "av_repeat_stop",
        // Pause, eye care and external apps
        "d_resume", "d_pause_grace", "ec_banner_dwell", "av_ext_dwell",
    ];

    [Fact]
    public void EveryLookTimeRow_IsOnTheDwellPage_ExactlyOnce()
    {
        var ids = IdsOn(new DwellPageSource(TestSettingsPageContext.Build()));

        Check.That(ids).Contains(LookTimeRowIds);
        Check.WithCustomMessage("a look-time row is registered twice on the Dwell page")
            .That(ids.Length).IsEqualTo(ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoLookTimeRow_IsStillOnAnotherTab()
    {
        ISettingsPageSource[] others =
        [
            new AdvancedPageSource(TestSettingsPageContext.Build()),
            new GeneralPageSource(TestSettingsPageContext.Build()),
            new EyeCarePageSource(TestSettingsPageContext.Build()),
        ];

        foreach (var source in others)
        {
            var ids = IdsOn(source);
            foreach (var id in LookTimeRowIds)
                Check.WithCustomMessage($"{id} still lives on {source.GetType().Name}")
                    .That(ids).Not.Contains(id);
        }
    }

    [Fact]
    public void EveryMovedRow_NamesItsSurfacesInItsDescription()
    {
        var rows = new DwellPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows)
            .Where(r => r.Id.Length > 0)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["d_tool"] = ["task switcher", "Quick Actions", "Backstage"],
            ["d_ManualSelection"] = ["Lupe", "manual selection"],
            ["d_menu"] = ["keyboard"],
            ["d_act"] = ["restart notice", "break banner"],
            ["d_pause_grace"] = ["toolbar", "menu"],
            ["d_resume"] = ["Resume"],
            ["av_drag_settle"] = ["drag"],
            ["av_drag_release"] = ["drag"],
            ["av_drag_right_edge"] = ["drag"],
            ["av_scroll_stop"] = ["scroll"],
            ["av_repeat_dwell"] = ["repeat"],
            ["av_repeat_stop"] = ["repeat"],
            ["av_backdrop_hide"] = ["backdrop"],
            ["g_restart_notice"] = ["restart"],
            ["ec_banner_dwell"] = ["banner"],
            ["av_ext_dwell"] = ["external"],
            ["d_ts_tile"] = ["task-switcher", "toolbar"],
        };

        foreach (var (id, needles) in required)
        {
            var row = rows[id];
            Check.WithCustomMessage($"{id} has no description").That(row.Description).IsNotNull();
            foreach (var needle in needles)
                Check.WithCustomMessage(
                        $"{id}'s description does not name \"{needle}\": \"{row.Description}\"")
                    .That(row.Description!.Contains(needle, StringComparison.OrdinalIgnoreCase)).IsTrue();
        }
    }

    private static string[] IdsOn(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows)
            .Select(r => r.Id)
            .Where(id => id.Length > 0)
            .ToArray();
}
