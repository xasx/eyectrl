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
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml", ["settings", "language-recovery"]),
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs", ["settings", "language-recovery"]),
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
        ("src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs", ["overlays"]),
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
        ("src/EyeCtrl.App/Services/SupervisionToggleService.cs", ["supervisor"])
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
        foreach (var (pattern, surfaces) in map)
        {
            if (!pattern.EndsWith("/**", StringComparison.Ordinal)
                && string.Equals(pattern, relativePath, StringComparison.Ordinal))
            {
                return surfaces;
            }
        }

        foreach (var (pattern, surfaces) in map)
        {
            if (pattern.EndsWith("/**", StringComparison.Ordinal)
                && relativePath.StartsWith(pattern[..^2], StringComparison.Ordinal))
            {
                return surfaces;
            }
        }

        return null;
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
            [("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs",
              """var a = T("settings.title"); var b = T("language-recovery.restarting");""")]);
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
