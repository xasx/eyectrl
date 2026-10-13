namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Pins the curated subset per tab as an ordered row sequence (kind + id for interactive rows;
/// LabelSpec twins pinned by a text predicate, their id is ""). General is built through ALL
/// THREE supervision branches, and Cursor through both ring-style branches, so conditional rows
/// cannot silently leave simple mode (spec §6, round-2 m4/m5; plan-review I6).
/// </summary>
public class SimpleModeCurationTests
{
    // The precedent fake, verbatim (GeneralPageSourceResetDriftTests.cs:98-102): the real
    // IStartupRegistrar is SetStartOnBoot(bool) + an IsRegistered property.
    private sealed class FakeStartupRegistrar : EyeCtrl.Core.Interfaces.IStartupRegistrar
    {
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) { }
    }

    // General is NOT here: its rows depend on the supervision service, so it is built only in
    // the three-branch theory below (plan-review r2 N3).
    public static TheoryData<string> SimpleTabs() => new() { "Cursor", "Dwell", "Keyboard" };

    [Theory]
    [MemberData(nameof(SimpleTabs))]
    public void SimpleMode_ShowsExactlyTheCuratedSequence(string tab)
    {
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = UiLanguage.German;            // pending-language note present
        settings.Cursor.RingStyle = CursorRingStyle.WeightedSandwich;
        var ctx = TestSettingsPageContext.Build(settings: settings);

        var rows = SimpleFilter(ctx, tab);

        Check.That(rows.Select(Describe)).ContainsExactly(ExpectedSequence(tab).ToArray());
    }

    [Fact]
    public void Cursor_AchromaticReticle_NoteIsSimple()
    {
        var settings = new EyeCtrlSettings
            { Cursor = { RingStyle = CursorRingStyle.AchromaticReticle } };
        var ctx = TestSettingsPageContext.Build(settings: settings);
        var rows = SimpleFilter(ctx, "Cursor");

        // Five entries: the picker's LabelSpec twin (the 12-char prefix "Circle Color") replaces
        // the Pick row (plan-review r2 N3.4, round-3 R3.2).
        Check.That(rows.Select(Describe))
            .ContainsExactly("Label:Circle Color", "Combo:c_ring_style",
                "Slider:c_opacity", "Slider:c_size", "Button:c_reset");
    }

    public static TheoryData<string> SupervisionBranches() => new() { "connected", "disconnected", "unavailable" };

    [Theory]
    [MemberData(nameof(SupervisionBranches))]
    public void SimpleMode_GeneralBuildsEverySupervisionBranch(string branch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_simplecur_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.General.UiLanguage = UiLanguage.German;
            var connected = branch == "connected";
            var installed = branch != "unavailable";
            if (installed)
            {
                settings.Supervision.Enabled = true;
                new SupervisionEnabledWriter(path).Persist(true);
            }
            var (_, toggle, _) = SupervisionComposition.Create(
                settings, path, new FakeStartupRegistrar(),
                enablePublishing: () => { },
                launchSupervisor: () => false,
                tryReachSupervisor: () => false,
                isSupervisorInstalled: () => installed,
                isLaunchRegistered: () => true,
                isSupervisorConnected: () => connected,
                runInBackground: work => work(),
                postToUi: work => work());
            var ctx = TestSettingsPageContext.Build(settings: settings, supervision: toggle);

            var rows = SimpleFilter(ctx, "General").Select(Describe).ToArray();

            // Disconnected adds exactly one row over connected: the retry button (the branch
            // difference IS the pin). Unavailable collapses to the note + no toggle.
            // FULL ordered sequences per branch with the REAL 12-char prefixes of the embedded
            // English table (round-3 R3.1 — placeholders made every branch red):
            //   pending note    "Wird nach Ne..." (strings.json:478)
            //   status, on      "On — EyeCtrl..." (settings.supervision.status.on)
            //   status, absent  "On — but the..." (settings.supervision.status.not_answering)
            //   unavailable     "Automatic Cr..." (settings.general.supervision.unavailable)
            var status = connected ? "Label:On — EyeCtrl" : "Label:On — but the";
            var expected = new List<string> { "Combo:g_language", "Label:Wird nach Ne",
                "Combo:g_theme", "Toggle:g_boot", "Toggle:g_pause", "Toggle:g_track" };
            if (branch == "unavailable")
                expected.AddRange(["Label:Automatic Cr", "Button:g_reset"]);
            else
            {
                expected.Add("Toggle:g_supervision");
                if (branch == "disconnected") expected.Add("Button:g_supervision_retry");
                expected.AddRange([status, "Button:g_reset"]);
            }
            Check.That(rows).ContainsExactly(expected.ToArray());
        }
        finally { File.Delete(path); }
    }

    [StaFact]   // WithPagesOf runs WPF layout (round-3 R3.3; ReviewSheetTests:308 precedent)
    public void SimpleTabIds_MatchReality_BothDirections()
    {
        // Spec S5.2/L9: every id in the set yields >=1 Simple row; no other source has any.
        // Route: WithPagesOf (ALREADY internal, HandChunkedPageFitTests.cs:1096) — it uses
        // FixturesOf, which gives Models a real registry and disposes the scope; the plain
        // Construct route would NullReferenceException on ModelsPageSource. Only
        // AllSourceTypes needs internalizing (this task edits that file — see Files).
        var width = HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
        foreach (var type in HandChunkedPageFitTests.AllSourceTypes())
        {
            var id = TabIdOf(type);
            var hasSimple = HandChunkedPageFitTests.WithPagesOf(type, width,
                pages => pages.SelectMany(p => p.Rows).Any(r => r.Simple));
            if (SimpleSettingsFilter.SimpleTabIds.Contains(id))
                Check.WithCustomMessage($"{id} is in SimpleTabIds but yields no Simple row")
                    .That(hasSimple).IsTrue();
            else
                Check.WithCustomMessage($"{id} is not in SimpleTabIds but has a Simple row")
                    .That(hasSimple).IsFalse();
        }
    }

    // The SettingsWindow _sources map inverted (SettingsWindow.xaml.cs:572-586): 14 ids are
    // the suffix-stripped type name, ActionMenu is the one special case. THROWS on an
    // unmapped type so a new source cannot silently skip the drift test.
    private static string TabIdOf(Type t) => t.Name switch
    {
        "ActionMenuPageSource" => "Actions",
        var n when n.EndsWith("PageSource", StringComparison.Ordinal)
            && n != "ActionMenuPageSource" => n[..^"PageSource".Length],
        _ => throw new InvalidOperationException($"{t.Name}: no tab id - teach the drift test")
    };

    [Fact]
    public void ResetGeneral_KeepsTheModeFlag()
    {
        // Spec §5.7 / plan-review I9: the General reset never touches the flag. Modelled on
        // ResetGeneral_KeepsTheLanguage in GeneralPageSourceResetDriftTests.
        var settings = new EyeCtrlSettings();
        settings.General.SimpleSettingsUi = true;
        var ctx = TestSettingsPageContext.Build(settings: settings, showModal: (_, act) => act());
        var reset = new GeneralPageSource(ctx).Build().SelectMany(p => p.Rows)
            .OfType<ButtonSpec>().Single(b => b.Id == "g_reset");

        reset.OnActivate();

        Check.That(settings.General.SimpleSettingsUi).IsTrue();
    }

    /// <summary>
    /// The curated sequences per tab, membership from spec §5.3 — NEVER edited to match output;
    /// only the ORDER may follow the sources' true build order (plan-review I6).
    /// </summary>
    /// <param name="tab">The simple tab (General is pinned in the supervision theory instead).</param>
    /// <returns>The expected "Kind:id" descriptions, in build order.</returns>
    private static string[] ExpectedSequence(string tab) => tab switch
    {
        // c_opacity builds before c_size (CursorPageSource.cs:79 vs :103).
        "Cursor" => ["Pick:c_color", "Combo:c_ring_style", "Slider:c_opacity", "Slider:c_size",
            "Button:c_reset"],
        "Dwell" => ["Slider:d_act", "Slider:d_menu", "Slider:d_tool", "Slider:d_key",
            "Slider:d_pred", "Slider:d_rad", "Toggle:d_cue_sound", "Button:d_reset"],
        "Keyboard" => ["Combo:k_layout", "Slider:k_size", "Toggle:k_pred", "Combo:k_lang",
            "Toggle:k_sound", "Button:k_reset"],
        _ => throw new ArgumentException($"Unknown simple tab {tab}")
    };

    private static SettingsRowSpec[] SimpleFilter(SettingsPageContext ctx, string tab) =>
        SimpleSettingsFilter.Apply(SourceFor(ctx, tab).Build())
            .SelectMany(p => p.Rows).ToArray();

    private static ISettingsPageSource SourceFor(SettingsPageContext ctx, string tab) => tab switch
    {
        "Cursor" => new CursorPageSource(ctx),
        "Dwell" => new DwellPageSource(ctx),
        "Keyboard" => new KeyboardPageSource(ctx),
        "General" => new GeneralPageSource(ctx),
        _ => throw new ArgumentException($"Unknown simple tab {tab}")
    };

    /// <summary>Interactive rows pin as "Kind:id"; LabelSpec rows (id "") pin by kind + a text
    /// prefix — the sanctioned label-twin rule (round-1 I4, round-2 m4).</summary>
    private static string Describe(SettingsRowSpec row) => row switch
    {
        SliderSpec s => $"Slider:{s.Id}",
        ToggleSpec t => $"Toggle:{t.Id}",
        ComboBoxSpec c => $"Combo:{c.Id}",
        ColorPickerSpec p => $"Pick:{p.Id}",
        ButtonSpec b => $"Button:{b.Id}",
        LabelSpec l => $"Label:{l.Text()[..Math.Min(12, l.Text().Length)]}",
        _ => row.GetType().Name
    };
}
