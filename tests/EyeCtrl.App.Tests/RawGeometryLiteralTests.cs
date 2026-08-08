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
/// <see cref="TheAllowList_IsPopulatedAndAcceptsRealTokenReferences"/> pins that surface,
/// because the migration to token-backed src/ is still under way: an allow-list that lost a
/// reference form would leave both guards below green while protecting nothing at all.
///
/// **The allow-list is partitioned by property group, not pooled.** A flat pool let a colour
/// token satisfy a font property — <c>FontSize = Tokens.CornerRadiusSmall</c> passed, and so
/// did <c>FontSize="{StaticResource BrushAccent}"</c>, which then throws
/// <c>XamlParseException</c> at load. <see cref="OverlayResourceResolutionTests"/> catches the
/// XAML half for the overlays it can construct, but not for <c>SettingsWindow</c> (7
/// non-optional constructor dependencies), so a type-wrong key written there would first
/// surface as a runtime throw in front of a user whose only input channel is an eye tracker.
/// The partition is derived from the member **names** by reflection for the same reason the
/// list itself is: a hard-coded partition can disagree with the surface it guards.
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
        // DwellArcButton is exempt on a FALSE POSITIVE, not on a literal. What the guard
        // reports is `FontSize=fontSize` at :115 — a pass-through parameter, not a value. The
        // file's *actual* literal is `public const double DefaultSymbolFontSize = 22;` at :15,
        // which this guard structurally CANNOT see: `\bFontSize\s*=` cannot match there
        // because FontSize is preceded by the `l` of `Symbol`, so there is no word boundary.
        // Task 12 must therefore not read a green guard as "nothing left here" — it deletes
        // that const and resolves the size from `dia * ContainerRatio.Small`. Note also that
        // Task 12's own replacement, `FontSize = symbolSize`, is a local and is *still* not
        // token-backed; assigning `fontSize ?? dia * ContainerRatio.Small` inline satisfies
        // the guard, whereas hoisting it into a local does not.
        ["src/EyeCtrl.Controls/DwellArcButton.cs"] = "PENDING Task 12",
        // Task 8 (App XAML) migrated; the two below still carry glyph sizes Task 12 owns:
        ["src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml"] = "PENDING Task 12",
        ["src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml"] = "PENDING Task 12",
        // Task 9 (App code-behind) migrated CornerRadius/FontSize(body)/FontWeight onto Tokens;
        // each of the five still carries a glyph/icon FontSize literal that Task 12 owns:
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] = "PENDING Task 12",
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] = "PENDING Task 12",
        ["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] = "PENDING Task 12",
        ["src/EyeCtrl.App/Overlays/EmojiGridManager.cs"] = "PENDING Task 12",
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] = "PENDING Task 12",
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
    /// is not read as a value.
    ///
    /// The unquoted value class excludes <c>,</c> <c>;</c> <c>)</c> <c>{</c> <c>}</c> <c>&gt;</c>
    /// but deliberately **not** newlines, so the wrapped form
    /// <c>CornerRadius = new CornerRadius(\n Tokens.X, Tokens.X, 0, 0)</c> — the exact shape
    /// Task 9 writes for the note title bar — still captures its first argument. Excluding
    /// newlines was measured: the capture becomes <c>new CornerRadius(</c>, which references no
    /// token, and a legitimately-migrated site is flagged.
    ///
    /// <c>{</c> and <c>}</c> are excluded because without them a <c>switch</c> expression
    /// cleared every arm: <c>FontSize = kind switch {\n 0 =&gt; Tokens.FontSizeTitle, ...</c>
    /// captured across the newline into the first arm, so one token arm vouched for all the
    /// literal ones. <c>&gt;</c> is excluded to bound a capture inside the XAML tag it started
    /// in: because the quoted alternation only recognises <c>"</c>, a **single-quoted**
    /// attribute fell through to the unquoted branch, and
    /// <c>&lt;Border FontSize='12' Background="{StaticResource BrushAccent}"/&gt;</c> captured
    /// as far as <c>/&gt;</c> — its own neighbour's token cleared the 12. Zero single-quoted
    /// attributes exist in the scanned projects today, which makes it latent rather than live.
    /// Measured, <c>{</c> is what truncates that particular example first; <c>&gt;</c> is what
    /// stops any such capture from running on into the next element regardless.
    ///
    /// <c>BorderThickness</c> is absent on purpose: it was migrated by the parent spec's §4.4
    /// and is guarded by TokenDictionaryTests.
    ///
    /// **Known limitation, recorded rather than hidden:** a value supplied other than by
    /// assignment — a <c>&lt;Setter Property="FontSize" Value="12"/&gt;</c>, or a positional
    /// constructor argument — is not matched. No such site exists in the two scanned projects
    /// today; if one is introduced, widen this pattern.
    ///
    /// **Second known limitation, also recorded rather than hidden.** Because the capture stops
    /// at the first <c>,</c>, a literal in a *later* argument of a call whose earlier argument
    /// is token-backed is still cleared: <c>Math.Max(dia * ContainerRatio.Small, 12)</c> passes
    /// while <c>Math.Max(12, dia * ContainerRatio.Small)</c> is flagged. The truncation cannot
    /// simply be removed, because the legitimate wrapped form above ends
    /// <c>Tokens.X, Tokens.X, 0, 0)</c> and a wider capture would flag it on those zeroes. No
    /// site of this shape exists or is planned; it is left open deliberately rather than closed
    /// by a rule that would fail a form the plan prescribes.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"\b(?<prop>CornerRadius|FontFamily|FontSize|FontWeight)\s*=(?![>=])\s*(?:""(?<v>[^""]*)""|(?<v>[^,;\)\{\}>]+))",
        RegexOptions.Compiled);

    /// <summary>
    /// A bare numeric literal: a digit not preceded by a word character or a dot.
    ///
    /// This is the structural half of the value check. Containing a token reference is not
    /// enough — <c>FontSize = compact ? 11 : Tokens.FontSizeBody</c> and
    /// <c>FontSize = 13 + Tokens.FontSizeBody</c> both reference the surface and both write a
    /// raw size, and both were cleared before this rule existed. The invariant the guard
    /// actually wants is "an expression containing a literal is an offender, regardless of what
    /// else it contains".
    ///
    /// The lookbehind is what keeps it from crying wolf: <c>ContainerRatio.Small</c>,
    /// <c>Tokens.FontSizeBodyLarge</c> and <c>BrushAccentDim10</c> all carry digits or
    /// digit-adjacent names, and none of them trips this.
    ///
    /// It does flag three forms the migration plan currently writes —
    /// <c>keySize * 1.5 * ContainerRatio.Medium</c>, <c>100 * ContainerRatio.Medium</c> and
    /// <c>80 * ContainerRatio.Large</c>. That is the intended verdict: those are magic numbers,
    /// and Task 12 already prescribes a named local const for the latter two. Hoist the number
    /// into a named const and the ratio expression passes.
    /// </summary>
    private static readonly Regex BareNumber = new(@"(?<![\w.])\d", RegexOptions.Compiled);

    /// <summary>Collapses whitespace so a wrapped value reports on one line.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The allowed reference forms **per property group**, built from the actual token surface.
    /// Reflection, not a hard-coded list: a stale allow-list is a whitelist's own failure mode.
    ///
    /// Keyed by the exact name the <c>prop</c> group captures, so a value is only ever checked
    /// against references that are type-compatible with the property being assigned. One flat
    /// pool let a colour token vouch for a font property, and a <c>{StaticResource BrushAccent}</c>
    /// written into a <c>FontSize</c> then throws at XAML load rather than failing a test.
    /// </summary>
    /// <returns>Allowed reference strings for each of the four guarded property names.</returns>
    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedByProperty()
    {
        // Every Tokens property is an allowed reference in both its C# and XAML form. That
        // already covers the derived keys (SmallCornerRadius, FontStandard, ...) because they
        // are Tokens properties — no second list to keep in step.
        //
        // The group a member belongs to is decided by its NAME, not by a hand-written list:
        // anything containing "CornerRadius" is a radius, anything starting with "Font" is
        // typography, and everything else — every colour, every brush, BorderThicknessStandard —
        // belongs to no group here and so satisfies none of the four properties. A member added
        // to Tokens under either naming convention is enrolled automatically; one added under
        // neither is admitted nowhere, which fails loudly at its first use rather than silently
        // widening the trust surface.
        var corner = new List<string>();
        var font = new List<string>();
        foreach (var p in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.Name == nameof(Tokens.Current)) continue;

            var group = p.Name.Contains("CornerRadius", StringComparison.Ordinal) ? corner
                : p.Name.StartsWith("Font", StringComparison.Ordinal) ? font
                : null;
            if (group is null) continue;

            group.Add($"Tokens.{p.Name}");
            group.Add($"{{StaticResource {p.Name}}}");
        }

        // ContainerRatio members are const, so they are fields rather than properties. A ratio
        // is a font size divided by its container, so it belongs to FontSize and to nothing
        // else — a ratio in a CornerRadius or a FontWeight is a mistake, not a style.
        var fontSizeOnly = new List<string>();
        foreach (var f in typeof(ContainerRatio).GetFields(BindingFlags.Public | BindingFlags.Static))
            fontSizeOnly.Add($"ContainerRatio.{f.Name}");

        // Exactly one glyph size has to reach XAML through {x:Static}: the tile icon inside
        // QuickActionsGridWindow's ItemTemplate, because a DataTemplate is instantiated per item
        // and cannot multiply by a ratio inline (Task 12). Every other glyph site assigns from
        // code-behind and needs no entry here. It is a size, so like the ratios it is offered to
        // FontSize alone. (Task 12 has not landed yet, so this loop finds nothing today; the
        // mechanism is kept live so that when TileIconSize appears it needs no test change.)
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
                    fontSizeOnly.Add($"{type.Name}.{prop.Name}");
        }

        return new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["CornerRadius"] = corner,
            ["FontFamily"] = font,
            ["FontWeight"] = font,
            ["FontSize"] = font.Concat(fontSizeOnly).ToList(),
        };
    }

    /// <summary>
    /// The union of every group's allowed forms — the guard's whole trust surface, for
    /// <see cref="TheAllowList_IsPopulatedAndAcceptsRealTokenReferences"/> to pin. Derived from
    /// <see cref="AllowedByProperty"/> so there is no second list that could disagree with it.
    /// </summary>
    /// <returns>The allowed reference strings.</returns>
    private static IReadOnlyCollection<string> AllowedReferences() =>
        AllowedByProperty().Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// True if the captured value references the token surface. **Only the value is examined,
    /// never its surroundings** — that is what stops a neighbouring token from clearing a
    /// literal.
    /// </summary>
    /// <param name="value">The captured right-hand side of the assignment.</param>
    /// <param name="allowed">Allowed reference forms for the property being assigned.</param>
    /// <returns>True if the value is token-backed.</returns>
    private static bool IsTokenBacked(string value, IReadOnlyCollection<string> allowed) =>
        allowed.Any(a => value.Contains(a, StringComparison.Ordinal));

    /// <summary>
    /// True if this assignment is an offender: its value either fails to reference a reference
    /// form allowed **for that property**, or references one and still writes a bare number.
    ///
    /// Both halves are needed. The first alone let a type-wrong token through; the second alone
    /// is what stops a token reference from vouching for a literal sitting next to it in the
    /// same expression.
    /// </summary>
    /// <param name="m">A match from <see cref="Assignment"/>.</param>
    /// <param name="allowed">The partitioned allow-list, from <see cref="AllowedByProperty"/>.</param>
    /// <returns>True if the site must be reported.</returns>
    private static bool IsOffender(
        Match m, IReadOnlyDictionary<string, IReadOnlyCollection<string>> allowed)
    {
        var value = m.Groups["v"].Value;

        // An unknown property name means the pattern grew a fifth property without this
        // partition growing with it. Offending is the safe direction: it fails loudly.
        if (!allowed.TryGetValue(m.Groups["prop"].Value, out var pool)) return true;

        return !IsTokenBacked(value, pool) || BareNumber.IsMatch(value);
    }

    /// <summary>
    /// The 1-based line the match starts on. Accurate only because
    /// <see cref="SourceScanner.StripComments"/> preserves line breaks — see its doc comment.
    /// </summary>
    /// <param name="text">The comment-stripped file text.</param>
    /// <param name="index">A character index into <paramref name="text"/>.</param>
    /// <returns>The line number.</returns>
    private static int LineOf(string text, int index) =>
        text.Take(index).Count(c => c == '\n') + 1;

    [Fact]
    public void NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens()
    {
        var allowed = AllowedByProperty();
        var offenders = new List<string>();

        foreach (var (relative, text) in SourceScanner.ScannedFiles())
        {
            if (Exempt.ContainsKey(relative)) continue;

            // Grouped by value, NOT de-duplicated: an earlier draft called .Distinct(), so two
            // identical CornerRadius="8" Borders in one file produced one byte-identical entry.
            // Fixing one of them then left the message unchanged, which is indistinguishable
            // from having made no progress at all — and with ~116 sites to migrate, a message
            // without line numbers costs a grep per fix.
            var bad = Assignment.Matches(text)
                .Where(m => IsOffender(m, allowed))
                .GroupBy(m => $"{m.Groups["prop"].Value}={Whitespace.Replace(m.Groups["v"].Value.Trim(), " ")}")
                .Select(g =>
                {
                    var lines = g.Select(m => LineOf(text, m.Index)).ToList();
                    var label = lines.Count == 1 ? "line" : "lines";
                    return $"{g.Key} ({label} {string.Join(", ", lines)})";
                })
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
        var allowed = AllowedByProperty();
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

            var any = Assignment.Matches(text).Any(m => IsOffender(m, allowed));
            if (!any)
                stale.Add($"{relative}: fully migrated — drop the exemption");
        }

        Check.WithCustomMessage("Stale exemptions:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }

    [Fact]
    public void TheAllowList_IsPopulatedAndAcceptsRealTokenReferences()
    {
        // A TRUNCATED allow-list would leave both guards above green while protecting nothing:
        // ContainerRatio has zero usages in src/, so losing all five of its forms is invisible
        // to them. (A total collapse is now caught by NoUIFile_, since src/EyeCtrl.Controls has
        // token-backed sites — but partial loss is not.) The breakage would otherwise first
        // appear as baffling failures midway through a migration task. This fails loudly instead.
        //
        // The union floor below is a coarse smoke test only, not a precision instrument: it sits
        // comfortably above every measured truncation, so it trips on a wholesale collapse and
        // nothing subtler. The per-form IsTokenBacked checks that follow are the actual pin,
        // because a reference *form* is what silently disappears when a BindingFlags slip or a
        // naming-convention change lands, and that is a different granularity than a count can
        // see. Measured concretely: if ContainerRatio's members became properties instead of
        // const fields, reflection would drop all five of them from every group, and the union
        // floor — and a per-group floor beside it — would still pass, because the loss never
        // crosses whatever threshold a floor happens to hold. Only the form checks below notice,
        // because they ask whether a specific form still resolves rather than how many are left.
        var byProp = AllowedByProperty();
        var allowed = AllowedReferences();

        const string collapsed =
            "The allow-list collapsed. Tokens' or ContainerRatio's shape changed — check the "
            + "BindingFlags, whether the members are still properties/fields, and whether their "
            + "names still carry the 'CornerRadius'/'Font' prefixes the partition keys off.";

        Check.WithCustomMessage(collapsed).That(allowed.Count).IsStrictlyGreaterThan(30);

        // Representative references must actually resolve — one per reference FORM, because a
        // form is what a BindingFlags or naming change silently drops.
        Check.WithCustomMessage("The C# Tokens form no longer resolves for CornerRadius.")
            .That(IsTokenBacked("Tokens.StandardCornerRadius", byProp["CornerRadius"])).IsTrue();
        Check.WithCustomMessage("The {StaticResource} form no longer resolves for FontSize.")
            .That(IsTokenBacked(
                "{StaticResource FontSizeBody}", byProp["FontSize"])).IsTrue();
        Check.WithCustomMessage("ContainerRatio no longer resolves for FontSize.")
            .That(IsTokenBacked("_keySize * ContainerRatio.Small", byProp["FontSize"])).IsTrue();
        Check.WithCustomMessage("The FontWeight tokens no longer resolve.")
            .That(IsTokenBacked("Tokens.FontWeightStrong", byProp["FontWeight"])).IsTrue();

        // Negatives, so the test cannot pass by IsTokenBacked returning true for everything —
        // and so the type partition itself is pinned, not merely present.
        Check.WithCustomMessage("A raw literal is being treated as token-backed.")
            .That(IsTokenBacked("12", byProp["FontSize"])).IsFalse();
        Check.WithCustomMessage(
                "A colour brush satisfies FontSize again. That value throws XamlParseException "
                + "at load, which no headless test would catch on SettingsWindow.")
            .That(IsTokenBacked("{StaticResource BrushAccent}", byProp["FontSize"])).IsFalse();
        Check.WithCustomMessage("A radius token satisfies FontSize again.")
            .That(IsTokenBacked("Tokens.CornerRadiusSmall", byProp["FontSize"])).IsFalse();
        Check.WithCustomMessage("A font-size token satisfies CornerRadius again.")
            .That(IsTokenBacked("Tokens.FontSizeBody", byProp["CornerRadius"])).IsFalse();
        Check.WithCustomMessage("A container ratio satisfies CornerRadius again.")
            .That(IsTokenBacked("ContainerRatio.Small", byProp["CornerRadius"])).IsFalse();
    }
}
