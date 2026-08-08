namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Repo-wide guard: no corner radius, font family, font size or font weight may be written
/// as a literal in UI source. Every one must come from
/// <see cref="EyeCtrl.Controls.Theme.Tokens"/> or <see cref="ContainerRatio"/>.
///
/// **This is a whitelist, and that is the whole point.** <see cref="RawColourLiteralTests"/>
/// enumerates *bad* forms, and commit 9e252e1 records the two holes that follow from it:
/// its pattern never matches <c>FromRgb</c> (the capital R), and it requires a literal first
/// argument, so a computed one escapes even once the casing is fixed. The second hole is
/// structural, not a typo — any blacklist has a form its author did not think of.
///
/// So this guard inverts the test: it finds every occurrence of the four property names and
/// allows only those that reference the token surface. A computed argument is an offender
/// like any other, because forms are never enumerated.
///
/// The allowed member names are obtained by **reflection** over Tokens and ContainerRatio,
/// never hard-coded: a whitelist's own failure mode is a stale allow-list, and reflecting
/// means this guard cannot disagree with the surface it guards.
///
/// No <see cref="RegexOptions.IgnorePatternWhitespace"/> — RawColourLiteralTests documents
/// how that mode makes whitespace inside a pattern silently insignificant.
/// </summary>
public class RawGeometryLiteralTests
{
    /// <summary>
    /// Files allowed to contain literals, each with the reason. A path listed here is exempt
    /// in full, so prefer migrating over listing.
    ///
    /// **During the migration this list holds the not-yet-migrated files, and nothing else.**
    /// Each entry tagged PENDING is removed by the task that migrates that file;
    /// <see cref="EveryExemption_NamesAFileThatStillHasALiteral"/> then fails if one lingers
    /// after its literals are gone. By the end of Task 12 the list is empty.
    ///
    /// **The token-declaration files need no permanent exemption, which was measured rather
    /// than assumed.** The plan provisioned seven — ITheme, ThemeBase, Tokens, the three
    /// themes and ContainerRatio — on the reasoning that a definition site must be allowed to
    /// hold the values it defines. <see cref="EveryExemption_NamesAFileThatStillHasALiteral"/>
    /// rejected all seven, correctly: every declaration there is a *suffixed* name
    /// (<c>CornerRadiusSmall</c>, <c>FontSizeBody</c>) with an *expression body*
    /// (<c>=&gt; 4.0</c>), so <see cref="Assignment"/> — which needs a bare property name
    /// followed by <c>=</c>, and which <c>(?![>=])</c> stops from reading <c>=&gt;</c> as
    /// assignment — cannot match a single site in them. Exemptions that protect nothing are
    /// exactly what the companion test exists to strip, so they were dropped instead of the
    /// test being loosened to accommodate them. If a theme file ever does grow a real
    /// assignment, the guard flags it loudly, which is the failure direction this design wants.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── PENDING migration. Remove each as its task lands. ───────────────────
        // Task 7 (Controls radii + ramp):
        ["src/EyeCtrl.Controls/DwellButton.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellToggle.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellComboBox.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellColorPicker.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellLabel.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellSlider.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellArcButton.cs"] = "PENDING Task 12",
        // Task 8 (App XAML):
        ["src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/SplashWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/CalibrationWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml"] = "PENDING Task 8",
        // Task 9 (App code-behind):
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/EmojiGridManager.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs"] = "PENDING Task 12",
    };

    /// <summary>
    /// A geometry or type assignment **and its value**, captured together.
    ///
    /// Capturing the value is the whole design. An earlier draft of this guard matched the
    /// property name alone and then scanned 200 characters forward for any token reference,
    /// which cleared a literal whenever an unrelated token happened to sit nearby. Measured
    /// against the real tree it flagged **0 of 6** literals in QuickActionsGridWindow.xaml —
    /// including the <c>CornerRadius="22"</c> circle site, cleared by a neighbouring
    /// <c>{StaticResource BrushSurfaceMedium}</c> — and 3 of 7 in TrackStatusWindow.xaml. Dense
    /// object initialisers are the dominant form in EyeCtrl.App/Overlays, so the window
    /// approach caught almost nothing while looking rigorous.
    ///
    /// <c>(?![>=])</c> stops <c>=&gt;</c> and <c>==</c> parsing as assignment, so an
    /// expression-bodied member such as <c>public static FontFamily FontStandard =&gt; ...</c>
    /// is not read as a value. The unquoted value class excludes <c>,</c> <c>;</c> <c>)</c> but
    /// deliberately **not** newlines, so the wrapped form
    /// <c>CornerRadius = new CornerRadius(\n Tokens.X, Tokens.X, 0, 0)</c> still captures its
    /// first argument.
    ///
    /// <c>BorderThickness</c> is absent on purpose: it was migrated by the parent spec's §4.4
    /// and is guarded by TokenDictionaryTests.
    ///
    /// **Known limitation, recorded rather than hidden:** a value supplied other than by
    /// assignment — a <c>&lt;Setter Property="FontSize" Value="12"/&gt;</c>, or a positional
    /// constructor argument — is not matched. No such site exists in the two scanned projects
    /// today; if one is introduced, widen this pattern.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"\b(?<prop>CornerRadius|FontFamily|FontSize|FontWeight)\s*=(?![>=])\s*(?:""(?<v>[^""]*)""|(?<v>[^,;\)]+))",
        RegexOptions.Compiled);

    /// <summary>
    /// Every allowed reference form, built from the actual token surface. Reflection, not a
    /// hard-coded list: a stale allow-list is a whitelist's own failure mode.
    /// </summary>
    /// <returns>The allowed reference strings.</returns>
    private static IReadOnlyCollection<string> AllowedReferences()
    {
        // Every Tokens property is an allowed reference in both its C# and XAML form. That
        // already covers the derived keys (SmallCornerRadius, FontStandard, ...) because they
        // are Tokens properties — no second list to keep in step.
        var names = new List<string>();
        foreach (var p in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.Name == nameof(Tokens.Current)) continue;
            names.Add($"Tokens.{p.Name}");
            names.Add($"{{StaticResource {p.Name}}}");
        }

        // ContainerRatio members are const, so they are fields rather than properties.
        foreach (var f in typeof(ContainerRatio).GetFields(BindingFlags.Public | BindingFlags.Static))
            names.Add($"ContainerRatio.{f.Name}");

        // Exactly one glyph size has to reach XAML through {x:Static}: the tile icon inside
        // QuickActionsGridWindow's ItemTemplate, because a DataTemplate is instantiated per item
        // and cannot multiply by a ratio inline (Task 12). Every other glyph site assigns from
        // code-behind and needs no entry here.
        //
        // Scoped to OverlayWindow SUBCLASSES, not the whole assembly. Reflecting over every type
        // would silently enrol any future public static double in EyeCtrl.App — measured, that
        // already admits three unrelated NoteLayout properties — and a guard whose trust surface
        // grows on its own is the wrong shape. Window classes are where {x:Static} consumers can
        // legitimately live, so that is the boundary.
        //
        // Still reflection rather than a hard-coded member name: a rename then produces a *false
        // positive* on the migrated site, which fails loudly, instead of a silent hole.
        foreach (var type in typeof(OverlayWindow).Assembly.GetTypes())
        {
            if (!typeof(OverlayWindow).IsAssignableFrom(type)) continue;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (prop.PropertyType == typeof(double))
                    names.Add($"{type.Name}.{prop.Name}");
        }

        return names;
    }

    /// <summary>
    /// True if the captured value references the token surface. **Only the value is examined,
    /// never its surroundings** — that is what stops a neighbouring token from clearing a
    /// literal.
    /// </summary>
    /// <param name="value">The captured right-hand side of the assignment.</param>
    /// <param name="allowed">Allowed reference forms, from <see cref="AllowedReferences"/>.</param>
    /// <returns>True if the value is token-backed.</returns>
    private static bool IsTokenBacked(string value, IReadOnlyCollection<string> allowed) =>
        allowed.Any(a => value.Contains(a, StringComparison.Ordinal));

    [Fact]
    public void NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens()
    {
        var allowed = AllowedReferences();
        var offenders = new List<string>();

        foreach (var (relative, text) in SourceScanner.ScannedFiles())
        {
            if (Exempt.ContainsKey(relative)) continue;

            var bad = Assignment.Matches(text)
                .Where(m => !IsTokenBacked(m.Groups["v"].Value, allowed))
                .Select(m => $"{m.Groups["prop"].Value}={m.Groups["v"].Value.Trim()}")
                .Distinct()
                .ToList();

            if (bad.Count > 0)
                offenders.Add($"{relative}: {string.Join(", ", bad)}");
        }

        Check.WithCustomMessage(
                "Geometry or type values written outside the tokens. Migrate them onto "
                + "EyeCtrl.Controls.Theme.Tokens or ContainerRatio, or add the file to Exempt "
                + "with a specific reason:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void EveryExemption_NamesAFileThatStillHasALiteral()
    {
        // Stops the allow-list rotting: an exemption for a file with nothing left to exempt
        // is protecting nothing and would hide a future literal. This is also what forces the
        // PENDING entries to be removed as the migration tasks land.
        var allowed = AllowedReferences();
        var byPath = SourceScanner.ScannedFiles().ToDictionary(f => f.Relative, f => f.Text,
            StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();

        foreach (var (relative, _) in Exempt)
        {
            if (!byPath.TryGetValue(relative, out var text))
            {
                stale.Add($"{relative}: file does not exist or is not scanned");
                continue;
            }

            var any = Assignment.Matches(text)
                .Any(m => !IsTokenBacked(m.Groups["v"].Value, allowed));
            if (!any)
                stale.Add($"{relative}: fully migrated — drop the exemption");
        }

        Check.WithCustomMessage("Stale exemptions:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }
}
