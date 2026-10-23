namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Tests.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Single-surface keys (critique-01 #2): a source file may only reference keys of the surfaces it
/// belongs to. Whole-surface gating is only as good as this rule — one key reused across two
/// surfaces is exactly the half-German screen the manifest exists to prevent.
///
/// <para>
/// A <c>FormatProviderFor("x")</c> literal is a reference to surface <c>x</c> too (T02 fixup-01):
/// a file that formats its numbers with another surface's provider shows German text with English
/// decimals from Step 2 until that other surface switches, and the wrong surface id is otherwise
/// invisible — a provider has no key to lint.
/// </para>
/// </summary>
/// <remarks>
/// Globs are used only where a folder really holds one surface. <c>App/Overlays/</c>,
/// <c>App/Services/</c> and <c>ViewModels/</c> mix surfaces, so their files are classified one by
/// one; a file that references a key without a classification fails. The map is extended by the
/// tickets that add the references (T04–T08); the entries below are the ones the plan names today.
/// </remarks>
public class SingleSurfaceKeyTests
{
    private static readonly (string Pattern, string[] Surfaces)[] SurfaceMap =
    [
        // Folders that really hold one surface.
        ("src/EyeCtrl.App/Views/Settings/**", ["settings"]),
        // The Supervisor's tray, setup window and FallenBack dialog are one surface (D4). Its
        // recovery strings are another (D5), and the Supervisor files that hold them are listed
        // explicitly by T05b — a glob must not grant both surfaces to every file.
        ("src/EyeCtrl.Supervisor/**", ["supervisor"]),

        // T05b: the Supervisor setup window's language row model holds the recovery strings (D5);
        // the window and every other Supervisor file stay on the supervisor surface alone.
        ("src/EyeCtrl.Supervisor/Supervision/LanguageSetupRow.cs", ["language-recovery"]),

        // T06: the startup-error box's two texts (title + labelled detail) resolve together.
        ("src/EyeCtrl.App/Services/StartupErrorText.cs", ["startup-error"]),

        // Mixed folders: explicit files only.
        // (T15 F9: no longer granted language-recovery. The restart notices moved to
        // RestartNotices.cs, and the window keeps only a comment about them.)
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml", ["settings"]),
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs", ["settings"]),
        // T07: the pager chrome's glyph/step/jump decisions include the D11 accessible-name keys;
        // the window resolves them. Settings chrome, so the settings surface.
        ("src/EyeCtrl.App/Views/SettingsPagerChrome.cs", ["settings"]),
        // T07: the Settings window's modal Yes/No labels live in the window's view model.
        ("src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs", ["settings"]),
        // T04: the General page holds the language row (language-recovery) among settings rows.
        ("src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs", ["settings", "language-recovery"]),
        // T04: the restart flow is language-recovery (D5) — its notices, its failure reasons,
        // and the failure frame the coordinator wraps around a raw exception message.
        ("src/EyeCtrl.App/Views/RestartNotices.cs", ["language-recovery"]),
        ("src/EyeCtrl.App/Services/SelfRestartStrategy.cs", ["language-recovery"]),
        ("src/EyeCtrl.App/Services/AppRestartCoordinator.cs", ["language-recovery"]),
        ("src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml", ["caregiver-alert"]),
        ("src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs", ["caregiver-alert"]),
        ("src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml", ["drift-notice"]),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml", ["calibration"]),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs", ["calibration"]),
        ("src/EyeCtrl.App/Overlays/CalibrationCompletion.cs", ["calibration"]),
        ("src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml", ["track-status"]),
        ("src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs", ["track-status"]),
        ("src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml", ["eye-care"]),
        ("src/EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml", ["eye-care"]),
        ("src/EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/EyeCareService.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/Exercises/Calibration202020Exercise.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/Exercises/DirectionalSweepExercise.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/Exercises/NearFarFocusExercise.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/Exercises/SaccadeExercise.cs", ["eye-care"]),
        ("src/EyeCtrl.EyeCare/Exercises/SmoothPursuitExercise.cs", ["eye-care"]),
        ("src/EyeCtrl.ViewModels/CalibrationFailureText.cs", ["calibration"]),
        // T08: the ring's key map lives in Core (the English lives in strings.json now), and the
        // class that resolves it, plus the App-side overlays hosts, reference the overlays keys.
        ("src/EyeCtrl.Core/Models/RingLabelMap.cs", ["overlays"]),
        ("src/EyeCtrl.Core/ActionMenu/RingLabelSource.cs", ["overlays"]),
        ("src/EyeCtrl.App/Localization/OverlayTextSources.cs", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/ToolbarBand.cs", ["overlays"]),
        // Task 10 (adaptation preview): the panel's rows resolve the overlays surface's
        // adaptationPreview keys in code-behind.
        ("src/EyeCtrl.App/Overlays/AdaptationPreviewWindow.xaml.cs", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs", ["overlays"]),
        // Sticky notes P3 D4: the Place-here window resolves its label from overlays keys.
        ("src/EyeCtrl.App/Overlays/NotePlaceHereWindow.cs", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/NoteNoticeWindow.cs", ["overlays"]),
        // Sticky notes P2 D5: the manager picks the chip's kind-dependent text from overlays keys.
        ("src/EyeCtrl.App/Services/NoteManager.cs", ["overlays"]),
        // Sticky notes P1 D6 + F3: the keyboard window's UIA HelpText announces note targeting
        // from an overlays key, resolved in the keyboard window's code-behind.
        ("src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs", ["overlays"]),
        // T0(a): the lag-mark button's label and its Marked confirmation are overlays keys.
        ("src/EyeCtrl.App/Overlays/LagMarkWindow.xaml.cs", ["overlays"]),
        // T15 F4: the emoji skin-tone selector's back key takes its spoken name from overlays.edge.back.
        ("src/EyeCtrl.App/Overlays/EmojiGridManager.cs", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml", ["overlays"]),
        ("src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml", ["overlays"]),
        ("src/EyeCtrl.App/Services/ActionMenuEdgeButtons.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/ActionMenuModifierButtons.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/KeyboardEdgeButtons.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/ManualSelectionEdgeButtons.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/ScrollModeController.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/Coordinators/DragCoordinator.cs", ["overlays"]),
        ("src/EyeCtrl.App/Services/Coordinators/RepeatModeController.cs", ["overlays"]),
        // T04: the tray's own items are app-tray, but the one language item is the recovery
        // path's entry point (D9) and takes its key from language-recovery (T06 expects this).
        ("src/EyeCtrl.App/Services/TrayIconManager.cs", ["app-tray", "language-recovery"]),
        // T07 fixup-01 C: the row's status and failure lines are Settings rows, so the service
        // speaks the settings surface (T06 review N1) — corrected from `supervisor`.
        ("src/EyeCtrl.App/Services/SupervisionToggleService.cs", ["settings"]),
        // T07: the Models page's download failures and the busy line render on the Settings
        // surface, so these App services speak `settings`.
        ("src/EyeCtrl.App/Services/ModelManagementService.cs", ["settings"]),
        // T07: the load-time calibration rejection sentences are shown only by the Settings
        // Calibration page (`LastLoadRejection`); the calibration window renders typed failures
        // instead. The settings surface owns them.
        ("src/EyeCtrl.Core/Calibration/CalibrationService.cs", ["settings"]),

        // SP-4b: the probe's coordinator resolves the C10 refusal reasons on the settings surface;
        // the probe window resolves its own screens on the overlays surface.
        ("src/EyeCtrl.App/Diagnostics/SubPixel/SubPixelProbeCoordinator.cs", ["settings"]),
        ("src/EyeCtrl.App/Diagnostics/SubPixel/SubPixelProbeWindow.cs", ["overlays"])
    ];

    /// <summary>
    /// A provider call's literal surface id. The literal is the whole reference: surface ids carry
    /// no dots, so the ordinary first-segment split below works unchanged.
    /// </summary>
    private static readonly Regex ProviderSurfaceReference = new(
        @"(?<![A-Za-z0-9_])FormatProviderFor\s*\(\s*""(?<surface>[^""]+)""",
        RegexOptions.Compiled);

    private static List<string> FindViolations(IEnumerable<(string Relative, string Text)> files)
    {
        var violations = new List<string>();
        foreach (var (relative, text) in files)
        {
            var references = StringKeyReferences.Extract(text)
                .Select(reference => (Key: reference.Key, Kind: reference.Kind))
                .Concat(ProviderSurfaceReference.Matches(text)
                    .Select(match => (Key: match.Groups["surface"].Value, Kind: "FormatProviderFor surface")))
                .ToList();
            if (references.Count == 0) continue;

            var allowed = AllowedSurfaces(SurfaceMap, relative);
            if (allowed is null)
            {
                violations.Add(
                    $"{relative}: references keys but is not classified in the surface map; add the file "
                    + "and the surfaces it may reference (a file spanning surfaces needs one key per surface, D3)");
                continue;
            }

            foreach (var reference in references)
            {
                var surface = reference.Key.Split('.')[0];
                if (!allowed.Contains(surface, StringComparer.Ordinal))
                {
                    violations.Add(
                        $"{relative}: {reference.Kind} references '{reference.Key}' (surface '{surface}'), "
                        + $"but this file may only reference: {string.Join(", ", allowed)}");
                }
            }
        }
        return violations;
    }

    /// <summary>
    /// Exact-file entries are checked before globs, so a file entry added after a covering glob
    /// still wins; the first exact match and then the first glob match are used.
    /// </summary>
    private static string[]? AllowedSurfaces(
        (string Pattern, string[] Surfaces)[] map,
        string relativePath)
    {
        var index = EntryIndexFor(map, relativePath);
        return index < 0 ? null : map[index].Surfaces;
    }

    private static int EntryIndexFor(
        (string Pattern, string[] Surfaces)[] map,
        string relativePath)
    {
        for (var index = 0; index < map.Length; index++)
        {
            var pattern = map[index].Pattern;
            if (!pattern.EndsWith("/**", StringComparison.Ordinal)
                && string.Equals(pattern, relativePath, StringComparison.Ordinal))
            {
                return index;
            }
        }

        for (var index = 0; index < map.Length; index++)
        {
            var pattern = map[index].Pattern;
            if (pattern.EndsWith("/**", StringComparison.Ordinal)
                && relativePath.StartsWith(pattern[..^2], StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The map's staleness check (T15 F9): a grant no file uses, or an entry no file matches. An
    /// unused grant is a door left open (a recovery key could enter the Settings chrome unnoticed),
    /// and the map had no way to say a grant had gone stale.
    /// </summary>
    private static List<string> StaleGrants(
        (string Pattern, string[] Surfaces)[] map,
        IEnumerable<(string Relative, string Text)> files)
    {
        var used = map.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        var matched = new bool[map.Length];
        foreach (var (relative, text) in files)
        {
            var index = EntryIndexFor(map, relative);
            if (index < 0) continue;
            matched[index] = true;
            foreach (var reference in StringKeyReferences.Extract(text))
                used[index].Add(reference.Key.Split('.')[0]);
            foreach (Match match in ProviderSurfaceReference.Matches(text))
                used[index].Add(match.Groups["surface"].Value);
        }

        var stale = new List<string>();
        for (var index = 0; index < map.Length; index++)
        {
            if (!matched[index])
            {
                stale.Add($"{map[index].Pattern}: matches no file under src/; remove the entry");
                continue;
            }

            foreach (var surface in map[index].Surfaces.Where(surface => !used[index].Contains(surface)))
                stale.Add($"{map[index].Pattern}: is granted '{surface}' but no matching file references it; drop the grant");
        }

        return stale;
    }

    [Fact]
    public void EveryKeyReference_InSource_StaysOnItsOwnSurface()
    {
        var violations = FindViolations(LocalizationTestData.SourceFiles());

        Check.WithCustomMessage(
                "A source file references a key outside the surface(s) it may use. Whole-surface "
                + "gating depends on one key belonging to exactly one surface; a shared producer must "
                + "take its text (or a key function) from its host instead (D3).\n  "
                + string.Join("\n  ", violations))
            .That(violations).IsEmpty();
    }

    [Fact]
    public void EveryGrantedSurface_IsUsedByAFileOfItsEntry()
    {
        var stale = StaleGrants(SurfaceMap, LocalizationTestData.SourceFiles());

        Check.WithCustomMessage(
                "The surface map holds a stale grant. A grant nothing uses lets a key of that surface "
                + "enter the file unnoticed; drop it (or the entry).\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }

    [Fact]
    public void TheStalenessCheck_FlagsAnUnusedGrant_AndAnEntryWithNoFile()
    {
        var map = new (string, string[])[]
        {
            ("src/X/File.cs", ["settings", "language-recovery"]),
            ("src/Y/**", ["overlays"]),
            ("src/Gone.cs", ["settings"])
        };

        var stale = StaleGrants(map,
        [
            ("src/X/File.cs", """var a = T("settings.title");"""),
            ("src/Y/One.cs", """var b = T("overlays.ring.back");"""),
        ]);

        Check.That(stale).HasSize(2);
        Check.That(stale[0]).Contains("src/X/File.cs").And.Contains("'language-recovery'");
        Check.That(stale[1]).Contains("src/Gone.cs").And.Contains("matches no file");

        // A provider call counts as using the surface, as the gate itself treats it.
        Check.That(StaleGrants(
            [("src/P.cs", ["settings", "calibration"])],
            [("src/P.cs", """var p = FormatProviderFor("calibration"); var t = T("settings.title");""")]))
            .IsEmpty();
    }

    [Fact]
    public void AFileInAOneSurfaceFolder_MayOnlyUseThatSurface()
    {
        Check.That(FindViolations(
            [("src/EyeCtrl.App/Views/Settings/AlphaPageSource.cs", """var t = T("settings.title");""")]))
            .IsEmpty();

        var violations = FindViolations(
            [("src/EyeCtrl.App/Views/Settings/AlphaPageSource.cs", """var t = T("overlays.ring.back");""")]);
        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("overlays.ring.back");
    }

    /// <summary>
    /// F3 (holistic review): an underscore key under a named call or a key map is a reference too,
    /// so the wrong-surface swap of a preset key (<c>settings.preset.kbd.select_all</c> →
    /// <c>overlays.…</c>, mutant M2) is a violation.
    /// </summary>
    [Fact]
    public void AnUnderscoreKey_OnTheWrongSurface_IsAViolation()
    {
        var violations = FindViolations(
            [
                ("src/EyeCtrl.App/Views/Settings/AlphaPageSource.cs",
                 """var a = StringTable.Current.T(target, "overlays.preset.kbd.select_all");"""),
                ("src/EyeCtrl.App/Views/Settings/BetaPageSource.cs",
                 """map["kbd.select_all"] = "overlays.preset.kbd.select_all";"""),
                ("src/EyeCtrl.App/Views/Settings/GammaPageSource.cs",
                 """var c = StringTable.Current.Template("overlays.control.combo.not_available");""")
            ]);

        Check.That(violations).HasSize(3);
        Check.That(violations.All(v => v.Contains("surface 'overlays'"))).IsTrue();
    }

    /// <summary>
    /// A provider call is a surface reference too (T02 fixup-01 F2): the F2 defect was exactly a
    /// Settings file formatting with the calibration provider, which no key lint would have seen.
    /// </summary>
    [Fact]
    public void AProviderSurfaceReference_ObeysTheSameSurfaceRule()
    {
        Check.That(FindViolations(
            [("src/EyeCtrl.App/Views/Settings/AlphaPageSource.cs",
              """var p = StringTable.Current.FormatProviderFor("settings");""")]))
            .IsEmpty();

        var violations = FindViolations(
            [("src/EyeCtrl.App/Views/Settings/AlphaPageSource.cs",
              """var p = StringTable.Current.FormatProviderFor("calibration");""")]);
        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("calibration");
    }

    /// <summary>F2's original defect, as a real-tree probe (not a fixture): the Settings tab's
    /// calibration sentence formatted with the calibration surface's provider.</summary>
    [Fact]
    public void TheSettingsCalibrationTab_FormatsWithTheSettingsSurface()
    {
        var text = LocalizationTestData.SourceFiles()
            .Single(f => f.Relative == "src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs")
            .Text;
        Check.WithCustomMessage(
                "CalibrationPageSource is a Settings-screen source; its provider must be the "
                + "settings surface's (F2).")
            .That(text.Contains("FormatProviderFor(\"settings\")", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void AFileSpanningTwoSurfaces_MayUseBoth()
    {
        var violations = FindViolations(
            [("src/EyeCtrl.App/Views/Settings/Sources/GeneralPageSource.cs",
              """var a = T("settings.title"); var b = T("language-recovery.restarting");""")]);

        // ...and the Settings window itself no longer may (T15 F9).
        Check.That(FindViolations(
            [("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs", """var b = T("language-recovery.restarting");""")]))
            .HasSize(1);
        Check.That(violations).IsEmpty();
    }

    [Fact]
    public void AnUnclassifiedFile_Fails()
    {
        var violations = FindViolations(
            [("src/EyeCtrl.App/Services/BrandNewService.cs", """var t = T("settings.title");""")]);

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("not classified");
    }

    [Fact]
    public void TheSupervisorFolder_AllowsOnlyTheSupervisorSurface()
    {
        Check.That(FindViolations(
            [("src/EyeCtrl.Supervisor/UI/SetupWindow.xaml.cs", """var a = T("supervisor.tray.start");""")]))
            .IsEmpty();

        // Recovery strings belong to files T05b lists explicitly, never to the whole folder glob.
        var violations = FindViolations(
            [("src/EyeCtrl.Supervisor/UI/SetupWindow.xaml.cs", """var a = T("language-recovery.restarting");""")]);
        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("language-recovery.restarting");
    }

    [Fact]
    public void ExactFileEntries_WinOverGlobs()
    {
        var map = new (string, string[])[]
        {
            ("src/X/**", ["overlays"]),
            ("src/X/File.cs", ["settings"])
        };

        Check.That(AllowedSurfaces(map, "src/X/File.cs")).ContainsExactly("settings");
        Check.That(AllowedSurfaces(map, "src/X/Other.cs")).ContainsExactly("overlays");
        Check.That(AllowedSurfaces(map, "src/Y/File.cs")).IsNull();
    }

    [Fact]
    public void TheMap_OnlyNamesDeclaredSurfaces()
    {
        var undeclared = SurfaceMap
            .SelectMany(entry => entry.Surfaces)
            .Where(surface => !EyeCtrl.Core.Localization.StringTableSchema.Surfaces.Contains(surface, StringComparer.Ordinal))
            .ToList();

        Check.That(undeclared).IsEmpty();
    }
}
