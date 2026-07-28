namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EyeCtrl.App.Overlays;
using NFluent;

/// <summary>
/// Drift guard: keeps the overlay inventory in
/// <c>docs/design/overlay-unification-spec.md</c> §3.1 honest.
///
/// The design spec's audit is only trustworthy if it lists every overlay. Twice
/// already an overlay escaped it — <see cref="NoteWindow"/>, which has no XAML file
/// and so was invisible to a <c>Overlays/*.xaml</c> sweep, and the pair added by
/// PRs #70/#71 (<see cref="CueOverlayWindow"/>, <see cref="DriftNoticeWindow"/>),
/// which landed after the audit was written. Both times the omission was silent.
///
/// This mirrors the existing <c>EyeCtrlSettingsEnumerationTests</c> convention:
/// a cheap reflection test that fails the build rather than letting documentation
/// rot unnoticed.
///
/// Not <c>Category=UI</c> — it only reflects over types and reads a file, so it
/// runs in the headless <c>Category!=UI</c> subset that CI executes.
/// </summary>
public class OverlayInventoryDriftTests
{
    private const string SpecRelativePath = "docs/design/overlay-unification-spec.md";

    /// <summary>Matches the Class column of a §3.1 table row: <c>| Name | `Type` | … |</c>.</summary>
    private static readonly Regex ClassCell = new(
        @"^\|[^|]+\|\s*`(?<cls>[A-Za-z0-9_]+)`\s*\|",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Walks up from the test assembly until it finds the repo root, identified by
    /// the solution file. Keeps the test independent of the bin/ layout.
    /// </summary>
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(OverlayInventoryDriftTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;

        Check.That(dir).IsNotNull();
        return dir!;
    }

    private static string SpecText()
    {
        var path = Path.Combine(RepoRoot().FullName, SpecRelativePath);
        Check.That(File.Exists(path)).IsTrue();
        return File.ReadAllText(path);
    }

    /// <summary>Every concrete <see cref="OverlayWindow"/> subclass shipped by EyeCtrl.App.</summary>
    private static IReadOnlyList<string> DeclaredOverlays() =>
        typeof(OverlayWindow).Assembly
            .GetTypes()
            .Where(t => t.IsClass
                        && !t.IsAbstract
                        && t != typeof(OverlayWindow)
                        && typeof(OverlayWindow).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Class names listed in the spec's §3.1 inventory table.</summary>
    private static IReadOnlyList<string> DocumentedOverlays()
    {
        var spec = SpecText();

        var start = spec.IndexOf("### 3.1 Overlay Backgrounds", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = spec.IndexOf("### 3.2", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(start);

        return ClassCell.Matches(spec[start..end])
            .Select(m => m.Groups["cls"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void EveryOverlayWindowSubclass_IsListedInSpecSection31()
    {
        var undocumented = DeclaredOverlays().Except(DocumentedOverlays(), StringComparer.Ordinal)
                                             .ToList();

        // Message names the offenders so the failure is actionable without a debugger.
        Check.WithCustomMessage(
                "These OverlayWindow subclasses are missing from §3.1 of "
                + SpecRelativePath + ": " + string.Join(", ", undocumented)
                + ". Add a row (Overlay | Class | Current Background | Format) and re-check "
                + "the counts in §1, §3.2 and the §5 migration phases.")
            .That(undocumented).IsEmpty();
    }

    [Fact]
    public void EverySpecListedOverlay_ResolvesToARealType()
    {
        var stale = DocumentedOverlays().Except(DeclaredOverlays(), StringComparer.Ordinal)
                                        .ToList();

        Check.WithCustomMessage(
                "§3.1 of " + SpecRelativePath + " lists overlays that no longer exist "
                + "(renamed or deleted): " + string.Join(", ", stale)
                + ". Remove or rename the rows.")
            .That(stale).IsEmpty();
    }

    [Fact]
    public void SpecOverlayCount_MatchesSection1Claim()
    {
        var declared = DeclaredOverlays().Count;
        var spec = SpecText();

        // §1 opens with "EyeCtrl has N overlay windows and M dwell controls."
        var match = Regex.Match(spec, @"EyeCtrl has (?<n>\d+) overlay windows");
        Check.That(match.Success).IsTrue();

        var claimed = int.Parse(match.Groups["n"].Value);
        Check.WithCustomMessage(
                $"§1 of {SpecRelativePath} claims {claimed} overlay windows but "
                + $"{declared} OverlayWindow subclasses exist. Update the opening sentence.")
            .That(claimed).IsEqualTo(declared);
    }
}
