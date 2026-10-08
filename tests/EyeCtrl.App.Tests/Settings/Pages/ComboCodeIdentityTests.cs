namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// T03a (German localization, story decision D7): every Settings combo saves a value CODE, never
/// its display text, so translating the display cannot change what is persisted. The nine combos
/// that used to pass <c>Code: null</c> now pass an explicit code array equal to the strings they
/// saved before this change; the three raw-enum combos also gained readable display names.
/// </summary>
public class ComboCodeIdentityTests
{
    /// <summary>
    /// The code arrays the nine newly-coded combos must keep. These are LITERALS — the strings the
    /// combos saved BEFORE this change (their old display text, or the enum member names) — pinned
    /// against fixed values rather than against the production arrays, so a changed code fails
    /// here. That is the one mistake this ticket exists to prevent: T07 translates Display next,
    /// and a code that drifted with it would silently change every saved value.
    /// </summary>
    private static readonly Dictionary<string, string[]> PreChangeSavedValues = new(StringComparer.Ordinal)
    {
        ["ad_posture"] = ["Conservative", "Balanced", "Responsive"],
        ["c_filter"] = ["Direct", "Smoothed", "SmoothedWithSnapping", "OneEuro"],
        ["c_pos_style"] = ["Crosshair", "Pin"],
        ["g_theme"] = ["Midnight", "Ember", "Contrast"],
        ["k_layout"] = ["QWERTY", "QWERTZ", "AZERTY"],
        ["k_edge"] = ["Bottom", "Top"],
        ["lay_pos"] = ["At Gaze", "At Center"],
        ["qa_edge"] = ["Left", "Right", "Top", "Bottom"],
        ["t_edge"] = ["Top", "Bottom", "Left", "Right"],
    };

    [Fact]
    public void TheNineNewlyCodedCombos_SaveExactlyTheStringsTheySavedBefore()
    {
        var combos = WalkableCombos(new EyeCtrlSettings()).ToDictionary(c => c.Id);

        foreach (var (id, expected) in PreChangeSavedValues)
        {
            Check.That(combos).ContainsKey(id);
            Check.That(combos[id].Code).ContainsExactly(expected);
        }
    }

    [Fact]
    public void Build_RawEnumCombos_ShowReadableNamesWhileCodesStayEnumMembers()
    {
        var combos = WalkableCombos(new EyeCtrlSettings()).ToDictionary(c => c.Id);

        // The one visible change of this ticket: the raw enum names become readable English while
        // the enum member names stay the codes. Display and Code are pinned as literals TOGETHER
        // so an enum reorder can never move both sides in step.
        Check.That(combos["ad_posture"].Display).ContainsExactly("Conservative", "Balanced", "Responsive");
        Check.That(combos["ad_posture"].Code).ContainsExactly("Conservative", "Balanced", "Responsive");
        Check.That(combos["c_filter"].Display).ContainsExactly(
            "Direct", "Smoothed", "Smoothed (Legacy)", "One Euro");
        Check.That(combos["c_filter"].Code).ContainsExactly(
            "Direct", "Smoothed", "SmoothedWithSnapping", "OneEuro");
        Check.That(combos["c_pos_style"].Display).ContainsExactly("Crosshair", "Pin");
        Check.That(combos["c_pos_style"].Code).ContainsExactly("Crosshair", "Pin");
    }

    [Fact]
    public void EveryComboOnEverySettingsPage_SavesACode_NotItsDisplayText()
    {
        // The done-when invariant, app-wide: a combo added tomorrow with `Code: null` fails here.
        var combos = WalkableCombos(new EyeCtrlSettings()).ToList();

        // Models is not in WalkableCombos: md_active is built only when a model is downloaded.
        using (var scope = ModelTestFakes.Create())
        {
            var (registry, mgmt) = scope.WithDownloadedBundled();
            var models = new ModelsPageSource(
                TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt));
            combos.AddRange(CombosOf(models));
        }

        Check.That(combos.Select(c => c.Id)).HasSize(15);
        foreach (var id in PreChangeSavedValues.Keys)
            Check.That(combos.Select(c => c.Id)).Contains(id);
        Check.That(combos.Select(c => c.Id)).Contains(
            "c_ring_style", "k_lang", "md_active", "s_mode", "av_backdrop_type");
        // T04's language row: saved codes are the UiLanguageTokens JSON tokens, never the
        // endonym display — selecting "Deutsch" must persist "german".
        Check.That(combos.Select(c => c.Id)).Contains("g_language");
        Check.That(combos.Single(c => c.Id == "g_language").Code)
            .ContainsExactly("system", "german", "english");

        foreach (var combo in combos)
        {
            Check.That(combo.Code).IsNotNull();
            Check.That(combo.Code!.Length).IsEqualTo(combo.Display.Length);
            Check.That(combo.Display.Length).IsStrictlyGreaterThan(0);
        }
    }

    [Fact]
    public void SettingsFileWrittenBeforeThisChange_LoadedWalkedEveryComboAndSaved_IsByteIdentical()
    {
        // (1) A settings file as the old build wrote it: one distinctive value per newly-coded
        // combo, every value spelled the way the old code saved it (the display text WAS the saved
        // text before this change). Sparse on purpose: Load fills everything else with defaults.
        const string preChangeFile = """
            {
              "viewPosition": "AtGaze",
              "cursor": { "filterMode": "OneEuro", "positionIndicatorStyle": "Pin" },
              "keyboard": { "activeLayout": "AZERTY", "screenEdge": "Top" },
              "toolbar": { "dockedEdge": "Left" },
              "theme": { "name": "Ember" },
              "quickActions": { "summonEdge": "Bottom" },
              "adaptation": { "posture": "Responsive" }
            }
            """;

        var dir = Path.Combine(Path.GetTempPath(), "eyectrl_t03a_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, preChangeFile);
            var loaded = new SettingsService(path).Load();

            // (2) Walk every combo row in scope: the row must still show the loaded value as
            // selected, and cycling every option proves each code round-trips through its setter.
            // Scoped to the nine: the five already-coded combos are untouched by this ticket, and
            // k_lang's getter maps the stored "" (auto-detect) to "en", so re-selecting it is not
            // value-preserving by design.
            var combos = WalkableCombos(loaded)
                .Where(c => PreChangeSavedValues.ContainsKey(c.Id)).ToList();
            Check.That(combos).HasSize(9);

            var service = new SettingsService(path);
            service.Save(loaded);   // canonical form of the loaded file
            var canonical = File.ReadAllText(path);

            foreach (var combo in combos)
            {
                var stored = combo.Get();
                Check.That(combo.Code!).Contains(stored);
                foreach (var code in combo.Code!)
                {
                    combo.Set(code);
                    Check.That(combo.Get()).IsEqualTo(code);
                }

                combo.Set(stored);
            }

            // (3) The saved JSON is byte-identical to what the load produced.
            service.Save(loaded);
            Check.That(File.ReadAllText(path)).IsEqualTo(canonical);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Every combo row of the sources a plain context can build, in tab order — including
    /// QuickActions, whose list page needs its real editor view model. Models is excluded:
    /// md_active exists only with a downloaded model (covered separately).
    /// </summary>
    private static IEnumerable<ComboBoxSpec> WalkableCombos(EyeCtrlSettings settings)
    {
        var ctx = TestSettingsPageContext.Build(settings);
        var sources = new ISettingsPageSource[]
        {
            new CursorPageSource(ctx),
            new DwellPageSource(ctx),
            new SelectionPageSource(ctx),
            new LayoutPageSource(ctx),
            new ActionMenuPageSource(ctx),
            new KeyboardPageSource(ctx),
            new ToolbarPageSource(ctx),
            new CalibrationPageSource(ctx),
            new GeneralPageSource(ctx),
            new EyeCarePageSource(ctx),
            new AdaptationPageSource(ctx),
            new AdvancedPageSource(ctx),
            new DiagnosticsPageSource(ctx),
        };

        foreach (var source in sources)
            foreach (var combo in CombosOf(source))
                yield return combo;

        var qa = new QuickActionsPageSource(
            TestSettingsPageContext.Build(settings, viewModel: QaTestSupport.MakeViewModel()));
        foreach (var combo in CombosOf(qa))
            yield return combo;
    }

    private static IEnumerable<ComboBoxSpec> CombosOf(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>();
}
