namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Tests.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Single-surface keys (critique-01 #2): a source file may only reference keys of the surfaces it
/// belongs to. Whole-surface gating is only as good as this rule — one key reused across two
/// surfaces is exactly the half-German screen the manifest exists to prevent.
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
        // The Supervisor's tray, setup window and FallenBack dialog are one surface (D4), and its
        // recovery strings are their own (D5) — the Supervisor process holds both.
        ("src/EyeCtrl.Supervisor/**", ["supervisor", "language-recovery"]),

        // Mixed folders: explicit files only.
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml", ["settings", "language-recovery"]),
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs", ["settings", "language-recovery"]),
        ("src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml", ["caregiver-alert"]),
        ("src/EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs", ["caregiver-alert"]),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml", ["calibration"]),
        ("src/EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs", ["calibration"]),
        ("src/EyeCtrl.App/Services/TrayIconManager.cs", ["app-tray"]),
        ("src/EyeCtrl.App/Services/SupervisionToggleService.cs", ["supervisor"])
    ];

    private static List<string> FindViolations(IEnumerable<(string Relative, string Text)> files)
    {
        var violations = new List<string>();
        foreach (var (relative, text) in files)
        {
            var references = StringKeyReferences.Extract(text);
            if (references.Count == 0) continue;

            var allowed = AllowedSurfaces(relative);
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

    private static string[]? AllowedSurfaces(string relativePath)
    {
        foreach (var (pattern, surfaces) in SurfaceMap)
        {
            if (pattern.EndsWith("/**", StringComparison.Ordinal))
            {
                if (relativePath.StartsWith(pattern[..^2], StringComparison.Ordinal)) return surfaces;
            }
            else if (string.Equals(pattern, relativePath, StringComparison.Ordinal))
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
    public void TheSupervisorFolder_AllowsBothOfItsSurfaces()
    {
        var violations = FindViolations(
            [("src/EyeCtrl.Supervisor/UI/RecoveryWindow.xaml.cs",
              """var a = T("supervisor.tray.start"); var b = T("language-recovery.restarting");""")]);
        Check.That(violations).IsEmpty();
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
