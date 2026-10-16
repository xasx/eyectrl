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
/// Task 11 added the second guard: §3.1's rows must each carry a D17 collision
/// classification (<c>collision-zone</c> | <c>exempt:&lt;reason&gt;</c>) as their
/// last cell. What is checked here is the hand-pinned <see cref="D17CollisionZoneClasses"/>
/// map, in both directions against the spec: every map entry must read
/// <c>collision-zone</c> in §3.1, and every <c>collision-zone</c> row must be backed by a
/// map entry. The map does NOT derive anything from <c>GazeConsumerInventory.Live</c> —
/// that each named source actually reports its rects via <c>Live()</c> is pinned per-source
/// in <c>GazeConsumerInventoryTests</c>, not here. A new overlay must make the call
/// explicitly — there is no silent default.
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
    /// Matches a full §3.1 row INCLUDING its trailing D17-classification cell (Task 11):
    /// <c>| Name | `Type` | … | collision-zone |</c> or <c>| … | exempt:reason |</c>. The
    /// classification must be the row's LAST cell so a background/format edit cannot displace it.
    /// The trailing <c>\r?$</c> (fix-wave F3) is load-bearing: .NET's <c>$</c> under
    /// <see cref="RegexOptions.Multiline"/> matches only before <c>\n</c>, so a CRLF row's
    /// <c>\r</c> broke the pattern on every CRLF checkout — CI's <c>actions/checkout</c> on
    /// <c>windows-latest</c> produces exactly that, and the guard died with "29 rows, but only 0
    /// carry a D17 classification".
    /// </summary>
    private static readonly Regex RowWithClassification = new(
        @"^\|[^|]+\|\s*`(?<cls>[A-Za-z0-9_]+)`[^\r\n]*\|\s*(?<d17>[^|\r\n]+?)\s*\|[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The only overlays whose rects the D17 gaze-consumer inventory reports (the preview panel's
    /// collision sources): the keyboard window, the edge-button bar (both instances — the shared
    /// bar and the action-menu modifier bar are the same window class), the action menu, the
    /// modes menu, the note window (its dwell buttons, via <c>NoteManager.LiveTargetRects</c>
    /// — review F1: notes are non-modal and freely positioned, so exempting them was silent
    /// unprotection of exactly the R4-H1 harm D17 exists to close), the restart-notice card
    /// (T15 merge: its close button via <c>RestartNoticeBridge.LiveTargetRects</c> — the card runs
    /// its own detector and is click-through and non-modal, the same shape as the notes) and the
    /// lag-mark button (second merge, #215: its grown hit area via <c>LagMarkBridge.LiveTargetRects</c>
    /// — the card's twin shape, an instrumented-session fixture with its own detector). External
    /// dwell's rects come from the SDK client apps via <c>ExternalDwellService.LiveTargetRects</c>,
    /// not from an overlay window of ours — so no window class here carries that source. This map
    /// is the drift guard's pinned half: a row being reclassified away from
    /// <c>collision-zone</c> fails the build instead of silently unprotecting a surface.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> D17CollisionZoneClasses =
        new Dictionary<string, string>
        {
            ["KeyboardWindow"] = "collision-zone",
            ["EdgeButtonBarWindow"] = "collision-zone",
            ["ActionMenuWindow"] = "collision-zone",
            ["ModesMenuWindow"] = "collision-zone",
            ["NoteWindow"] = "collision-zone",
            ["RestartNoticeWindow"] = "collision-zone",
            ["LagMarkWindow"] = "collision-zone",
        };

    /// <summary>The classification cell grammar: exactly <c>collision-zone</c>, or an exemption
    /// whose reason is a non-empty, pipe-free token run (<c>exempt:reason words</c>).</summary>
    private static readonly Regex ValidClassification = new(
        @"^(collision-zone|exempt:\S.*)$", RegexOptions.Compiled);

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

    // ── Task 11: the D17 collision classification (§3.1's fifth column) ──

    /// <summary>Class-name → classification-cell pairs, parsed from §3.1. Rows without a valid
    /// classification cell are absent here.</summary>
    private static IReadOnlyDictionary<string, string> SpecClassifications() =>
        SpecClassifications(SpecText());

    /// <summary>Text-injected form (fix-wave F3): lets the CRLF round-trip test re-run the exact
    /// production parse over the doc's bytes converted to CRLF.</summary>
    private static IReadOnlyDictionary<string, string> SpecClassifications(string spec)
    {
        var start = spec.IndexOf("### 3.1 Overlay Backgrounds", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = spec.IndexOf("### 3.2", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(start);

        return RowWithClassification.Matches(spec[start..end])
            .GroupBy(m => m.Groups["cls"].Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Groups["d17"].Value, StringComparer.Ordinal);
    }

    [Fact]
    public void EverySpecRow_CarriesAValidD17Classification()
    {
        // The classification drift guard: every §3.1 row must end with a D17 cell that is either
        // `collision-zone` or `exempt:<reason>`. Deleting the cell (or the whole trailing column
        // from a row) turns the row invisible to the classification map, which must fail here —
        // the same way a missing row fails the inventory test above.
        var spec = SpecText();

        var start = spec.IndexOf("### 3.1 Overlay Backgrounds", StringComparison.Ordinal);
        var end = spec.IndexOf("### 3.2", start, StringComparison.Ordinal);
        var totalRows = ClassCell.Matches(spec[start..end]).Count;
        var classified = SpecClassifications();

        var missing = DocumentedOverlays().Except(classified.Keys, StringComparer.Ordinal).ToList();

        Check.WithCustomMessage(
                $"§3.1 of {SpecRelativePath}: {totalRows} rows, but only {classified.Count} carry " +
                "a D17 classification as their last cell. Missing: " + string.Join(", ", missing)
                + ". Every row needs a trailing `collision-zone` or `exempt:<reason>` cell.")
            .That(missing).IsEmpty();

        var invalid = classified
            .Where(kv => !ValidClassification.IsMatch(kv.Value))
            .Select(kv => $"{kv.Key} = '{kv.Value}'")
            .ToList();
        Check.WithCustomMessage(
                "§3.1's D17 cells must be exactly `collision-zone` or `exempt:<reason>`. Invalid: "
                + string.Join(", ", invalid))
            .That(invalid).IsEmpty();
    }

    [Fact]
    public void D17NamedSources_AreClassifiedCollisionZone()
    {
        // The pinned half of the classification: the overlay classes pinned in
        // D17CollisionZoneClasses must read `collision-zone` in the spec. Reclassifying one as
        // exempt (the silent-unprotection drift) fails here even though the row and its cell are
        // well-formed.
        var spec = SpecClassifications();

        var drifted = D17CollisionZoneClasses
            .Where(kv => spec.TryGetValue(kv.Key, out var value) && value != kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        Check.WithCustomMessage(
                "These overlay classes are pinned as D17 collision sources " +
                "(D17CollisionZoneClasses) but §3.1 classifies them as something else: "
                + string.Join(", ", drifted)
                + ". Either the spec row or the pinned map has drifted.")
            .That(drifted).IsEmpty();
    }

    [Fact]
    public void D17CollisionZoneClassification_NamesARealOverlaySource()
    {
        // The reverse direction: a `collision-zone` row whose class is NOT pinned in
        // D17CollisionZoneClasses is a claim nothing enforces — either the pinned map is
        // missing an entry (a real gap: the class's source reports rects via Live(), but
        // nothing classifies them) or the classification lies. Both need a human, so both fail
        // the build.
        var spec = SpecClassifications();

        var unbacked = spec
            .Where(kv => kv.Value == "collision-zone")
            .Select(kv => kv.Key)
            .Except(D17CollisionZoneClasses.Keys, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "§3.1 classifies these overlays as collision-zone, but they are not pinned in " +
                "D17CollisionZoneClasses: " + string.Join(", ", unbacked)
                + ". Add the class to D17CollisionZoneClasses (and make sure its source reports " +
                "via Live() — covered by GazeConsumerInventoryTests) or reclassify the row as " +
                "exempt:<reason>.")
            .That(unbacked).IsEmpty();
    }

    // ── fix-wave F3: the pattern must survive a CRLF checkout (CI's actions/checkout on
    // windows-latest writes CRLF; the shared worktree holds the doc as LF, which is why nobody
    // saw the guard die there) ──

    [Fact]
    public void RowWithClassification_MatchesACrlfSuffixedRow()
    {
        // Unit-level proof of the CRLF bug: .NET's `$` under Multiline matches only before `\n`,
        // so a CRLF row's `\r` sat between the classification cell and the `$` — the pattern
        // matched zero rows and CI read "29 rows, but only 0 carry a D17 classification".
        const string row = "| Reset | `ToolbarWindow` | SurfaceDark | strip | collision-zone |\r\n";
        var match = RowWithClassification.Match(row);

        Check.That(match.Success).IsTrue();
        Check.That(match.Groups["cls"].Value).IsEqualTo("ToolbarWindow");
        Check.That(match.Groups["d17"].Value).IsEqualTo("collision-zone");
    }

    [Fact]
    public void EverySpecRow_CarriesAValidD17Classification_UnderCrlfToo()
    {
        // The round-trip proof: the doc's bytes normalized to CRLF (exactly what a fresh
        // windows-latest checkout holds), pushed through the SAME production parse — every row
        // must still classify, with the same map as the LF original.
        //
        // The LF side is normalized to `\n` EXPLICITLY (follow-up N1): `SpecText()` returns the
        // doc AS CHECKED OUT, which a CRLF checkout (CI) already holds as CRLF — both sides then
        // parse the same bytes, and a pattern that matches zero rows on both passed `0 == 0`.
        // The count guard below closes the remaining hole: a zero-parse can never equal its way
        // to green, whichever line ending the checkout holds.
        var lfText = SpecText().Replace("\r\n", "\n", StringComparison.Ordinal);
        var lf = SpecClassifications(lfText);
        Check.WithCustomMessage(
                "The LF side of the CRLF round-trip parsed zero rows — the pattern matches " +
                "nothing even in the doc normalized to LF, so the count comparison below would " +
                "be vacuous.")
            .That(lf.Count).IsStrictlyGreaterThan(0);
        var crlf = SpecClassifications(lfText.Replace("\n", "\r\n", StringComparison.Ordinal));

        Check.WithCustomMessage(
                "Every §3.1 row must carry its D17 classification under a CRLF checkout too — " +
                $"the LF parse found {lf.Count}, the CRLF parse only {crlf.Count}.")
            .That(crlf.Count).IsEqualTo(lf.Count);
        foreach (var (cls, classification) in lf)
        {
            Check.WithCustomMessage($"Row {cls} classified '{classification}' under LF but not under CRLF.")
                .That(crlf.ContainsKey(cls)).IsTrue();
        }
    }
}
