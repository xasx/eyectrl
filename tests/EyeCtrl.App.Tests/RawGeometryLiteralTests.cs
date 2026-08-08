namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
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
/// XAML half for the overlays it can construct, but not for <c>SettingsWindow</c> (8
/// non-optional constructor dependencies), so a type-wrong key written there would first
/// surface as a runtime throw in front of a user whose only input channel is an eye tracker.
/// The partition is derived by reflection for the same reason the list itself is: a hard-coded
/// partition can disagree with the surface it guards.
///
/// **The partition keys off the member's reflected CLR TYPE as well as its name**, because the
/// name-only version was not type-safe within the typography group: it pooled every member
/// beginning with "Font" and offered the pool to all three font properties, so
/// <c>FontSize="{StaticResource FontWeightStrong}"</c> and
/// <c>FontWeight="{StaticResource FontFamilyStandard}"</c> both passed — and both throw
/// <c>XamlParseException</c> at load. Type alone cannot replace the name either: <c>FontSize</c>
/// takes a <c>double</c> and so do the four raw <c>Tokens.CornerRadius*</c> doubles. See
/// <see cref="AllowedByProperty"/> for how the two halves divide the work.
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
    /// **The migration is finished and this list is empty.** While it ran, the list held the
    /// not-yet-migrated files and nothing else, one entry per file, each dropped by the task
    /// that migrated it; <see cref="EveryExemption_NamesAFileThatStillHasALiteral"/> fails if
    /// one lingers after its literals are gone, which is what forced them out. The last nine
    /// went with Task 12, so an entry appearing here now is a regression rather than a step.
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
        // EMPTY, and that is the finished state. Task 12 migrated the last nine files onto
        // ContainerRatio, so every scanned file's geometry and typography values now come from
        // the token surface. Any new entry is a regression, not a step in a migration: it means
        // a literal was written and waved through. Prefer migrating the value.
        //
        // A note for whoever considers adding one back: DwellArcButton's remaining site,
        // `FontSize = fontSize ?? dia * (dia < BadgeDiameterThreshold ? ...)`, passes because
        // the capture stops at the first `)` and what precedes it is ratio-backed with no bare
        // digit. Hoisting that expression into a local would break it — a local name references
        // no token. The same is true of DragTrailOverlay's IconSize, which is why that file
        // repeats its expression at the FontSize site instead of reading the const.
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
    /// The unquoted value class excludes <c>,</c> <c>;</c> <c>{</c> <c>}</c> <c>&gt;</c> and the
    /// two parentheses — the parentheses only because the balanced alternative beside it consumes
    /// them in matched pairs — but deliberately **not** newlines, so the wrapped form
    /// <c>CornerRadius = new CornerRadius(\n Tokens.X, Tokens.X, 0, 0)</c> — the exact shape
    /// Task 9 writes for the note title bar — is captured entire. Excluding newlines was
    /// measured: the capture becomes <c>new CornerRadius(</c>, which references no
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
    /// **The capture is paren-balanced, and that is what closes the later-argument hole.** An
    /// earlier version stopped the value at the first <c>,</c> and at the first <c>)</c>, so a
    /// literal in a *later* argument of a call whose *earlier* argument was token-backed was
    /// cleared outright: <c>FontSize = Math.Max(Tokens.FontSizeBody, 99)</c> captured only
    /// <c>Math.Max(Tokens.FontSizeBody</c> and passed. That shape is not hypothetical — it is
    /// what the four clamped glyph sites write
    /// (<c>Math.Max(keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize)</c>), so
    /// somebody writing the bare <c>12</c> instead of the named const would have been waved
    /// through at a site the codebase actively edits.
    ///
    /// The alternation now consumes a whole parenthesised argument list as part of the value but
    /// still stops at a comma **at nesting depth zero**. That distinction is the entire point,
    /// and it is what keeps the fix from reintroducing the far worse hole the value capture
    /// exists to close:
    /// <list type="bullet">
    /// <item><c>Math.Max(a, b)</c> is one value, comma and all, so both arguments are checked;</item>
    /// <item><c>FontSize = someLocal, FontFamily = Tokens.FontStandard</c> is still two
    /// assignments — measured, the first captures <c>someLocal</c> and nothing more, so the
    /// neighbour's token cannot vouch for it.</item>
    /// </list>
    /// Measured across the whole scanned tree, exactly six sites capture differently than under
    /// the truncating version, and five of them merely gain their closing <c>)</c> or their
    /// (token-backed) second argument. The sixth is the wrapped <c>CornerRadius</c> form below,
    /// which is why <see cref="BareNumber"/> exempts a standalone zero — see its doc comment for
    /// that trade and what it costs.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"\b(?<prop>CornerRadius|FontFamily|FontSize|FontWeight)\s*=(?![>=])\s*"
        + @"(?:""(?<v>[^""]*)""|(?<v>(?:[^,;()\{\}>]|\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\))+))",
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
    ///
    /// **A standalone <c>0</c> is exempt, and that exemption is a priced trade, not an oversight.**
    /// Once <see cref="Assignment"/> became paren-balanced it captures whole argument lists, and
    /// the one legitimate site in the tree that changed shape is the note title bar's
    /// <c>new CornerRadius(Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard, 0, 0)</c> —
    /// the asymmetric form the parent spec prescribes. Without the exemption a correctly-migrated,
    /// spec-mandated site fails on those zeroes.
    ///
    /// What it costs: <c>Math.Max(token, 0)</c> now passes, as would any token-backed expression
    /// whose only literal is a bare zero. That is a much smaller surface than the one the balance
    /// change closed, because zero is not a magic size — it means "none". An expression whose
    /// *whole* value is <c>0</c> is still an offender: it references no token, so it fails the
    /// first half of <see cref="IsOffender"/> regardless of this rule. The exemption is deliberately
    /// narrow: only a <c>0</c> not adjoined by another digit, a letter or a dot, so <c>0.5</c>,
    /// <c>07</c> and <c>0x20</c> are all still flagged.
    /// </summary>
    private static readonly Regex BareNumber = new(@"(?<![\w.])(?!0(?![\w.]))\d", RegexOptions.Compiled);

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
        // **A member is admitted by its ROLE *and* its CLR TYPE, and it takes both.** Name alone
        // was the original rule and it was not type-safe: every member whose name began with
        // "Font" landed in one pooled typography group offered to all three font properties, so
        // FontSize="{StaticResource FontWeightStrong}" and FontWeight="{StaticResource
        // FontFamilyStandard}" both passed — each of which is invalid XAML that throws
        // XamlParseException at load, in front of a user whose only input channel is an eye
        // tracker. Type alone does not work either: FontSize takes a double and so do the four
        // raw Tokens.CornerRadius* doubles, so a pure type partition re-admits
        // FontSize = Tokens.CornerRadiusSmall — one of the exact cases the partition was added
        // to reject. So the role narrows by name (what the member is FOR) and the type narrows
        // within it (what a WPF setter will actually accept).
        //
        // Role, from the name, exactly as before: anything containing "CornerRadius" is a radius,
        // anything starting with "Font" is typography, and everything else — every colour, every
        // brush, BorderThicknessStandard — belongs to no role and so satisfies none of the four
        // properties. A member added to Tokens under either convention is enrolled automatically;
        // one added under neither is admitted nowhere, which fails loudly at its first use rather
        // than silently widening the trust surface. Both halves are still reflected, never
        // hard-coded: a hard-coded partition can disagree with the surface it guards.
        var radius = new List<PropertyInfo>();
        var typography = new List<PropertyInfo>();
        foreach (var p in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.Name == nameof(Tokens.Current)) continue;

            if (p.Name.Contains("CornerRadius", StringComparison.Ordinal)) radius.Add(p);
            else if (p.Name.StartsWith("Font", StringComparison.Ordinal)) typography.Add(p);
        }

        // Both reference forms of every member of a role whose CLR type one of these WPF setters
        // accepts.
        //
        // Border.CornerRadius takes a CornerRadius, which covers the four derived
        // Tokens.XxxCornerRadius keys directly; the four raw Tokens.CornerRadiusXxx doubles are
        // admitted too because the C# form of that assignment is
        // `new CornerRadius(Tokens.CornerRadiusStandard, ...)`, where the double IS the value the
        // capture sees. FontSize takes a double. FontWeight takes a FontWeight — a double there
        // does not even compile in C# and throws in XAML. FontFamily takes a FontFamily, plus
        // string for the XAML converter path, which is what the FontFamilyStandard resource key
        // is: the raw chain string rather than the constructed family.
        static List<string> Forms(IEnumerable<PropertyInfo> role, params Type[] accepted) =>
            role.Where(p => accepted.Contains(p.PropertyType))
                .SelectMany(p => new[] { $"Tokens.{p.Name}", $"{{StaticResource {p.Name}}}" })
                .ToList();

        var corner = Forms(radius, typeof(CornerRadius), typeof(double));
        var fontSize = Forms(typography, typeof(double));
        var fontWeight = Forms(typography, typeof(FontWeight));
        var fontFamily = Forms(typography, typeof(FontFamily), typeof(string));

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
        // FontSize alone. (Task 12 has landed, so this loop finds exactly one member today:
        // QuickActionsGridWindow.TileIconSize, which is the only public static double property on
        // any OverlayWindow subclass. It needed no test change, which was the point of keeping
        // the mechanism live while it was still empty.)
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
            ["FontFamily"] = fontFamily,
            ["FontWeight"] = fontWeight,
            ["FontSize"] = fontSize.Concat(fontSizeOnly).ToList(),
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
    ///
    /// A reference must appear **complete**, not as a substring. Plain <c>Contains</c> was the
    /// original rule and it let any longer identifier ending in an allowed one satisfy the
    /// guard: a <c>LegacyTokens.FontSizeBody</c>, a <c>MyTokens.FontSizeBody</c>, anything at all
    /// suffixed with a real form. That is a whitelist accepting a name it never enrolled, which
    /// is the one thing a whitelist must not do. Requiring a boundary on both sides costs nothing
    /// real: no site in the tree writes a fully-qualified <c>Theme.Tokens.X</c>, and the one
    /// occurrence of that spelling is inside a comment, which the scanner strips.
    /// </summary>
    /// <param name="value">The captured right-hand side of the assignment.</param>
    /// <param name="allowed">Allowed reference forms for the property being assigned.</param>
    /// <returns>True if the value is token-backed.</returns>
    private static bool IsTokenBacked(string value, IReadOnlyCollection<string> allowed) =>
        allowed.Any(a => ContainsCompleteReference(value, a));

    /// <summary>
    /// True if <paramref name="reference"/> occurs in <paramref name="value"/> as a whole
    /// reference rather than as the tail of a longer one.
    ///
    /// A dot counts as a preceding word character, which is the whole point: it is what
    /// distinguishes <c>Tokens.FontSizeBody</c> from <c>LegacyTokens.FontSizeBody</c> and from
    /// any other type that happens to expose a member of the same name.
    /// </summary>
    /// <param name="value">The captured value.</param>
    /// <param name="reference">One allowed reference form.</param>
    /// <returns>True if the complete reference is present.</returns>
    private static bool ContainsCompleteReference(string value, string reference)
    {
        static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

        for (var i = value.IndexOf(reference, StringComparison.Ordinal);
             i >= 0;
             i = value.IndexOf(reference, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 ? ' ' : value[i - 1];
            var afterAt = i + reference.Length;
            var after = afterAt >= value.Length ? ' ' : value[afterAt];

            if (!IsNamePart(before) && before != '.' && !IsNamePart(after)) return true;
        }

        return false;
    }

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
        // is protecting nothing and would hide a future literal. This is also what forced each
        // migration task to drop its own entry as it landed. Vacuously true now that the
        // dictionary is empty, and that is the finished state, not a gap.
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
        // Why this test still earns its place now that the migration has landed. When it was
        // written, ContainerRatio had zero usages in src/, so losing all five of its forms was
        // invisible to both guards above. That is no longer the state: src/ reads ContainerRatio
        // 19 times across 9 files (counted outside comments), and every one of the five ratios is
        // read by at least one of them, so dropping any ratio form now DOES fail NoUIFile_ — as a
        // wall of false positives at correctly-migrated sites, naming the sites rather than the
        // cause. This test names the cause.
        //
        // The type PARTITION is a different matter, and this is the only guard on it at any
        // granularity. A partition that wrongly pools the groups only ever WIDENS trust, so it
        // produces no failure anywhere else: NoUIFile_ reports fewer offenders, not more. The
        // negatives at the bottom of this test are what catch it.
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
            + "BindingFlags, whether the members are still properties/fields, whether their "
            + "names still carry the 'CornerRadius'/'Font' prefixes the partition's ROLE half "
            + "keys off, and whether their CLR types still match the ones its TYPE half accepts.";

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
        Check.WithCustomMessage("The FontFamily token no longer resolves.")
            .That(IsTokenBacked("Tokens.FontStandard", byProp["FontFamily"])).IsTrue();
        Check.WithCustomMessage(
                "The raw CornerRadius doubles no longer resolve for CornerRadius. They must: the "
                + "C# form of that assignment passes them to new CornerRadius(...), so the double "
                + "is what the value capture sees.")
            .That(IsTokenBacked(
                "new CornerRadius(Tokens.CornerRadiusStandard)", byProp["CornerRadius"])).IsTrue();

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

    /// <summary>
    /// Every reference form that is the RIGHT role but the WRONG CLR type, one per guarded
    /// property, plus the substring case. Each of these passed before the partition keyed off the
    /// reflected type, and each is a value that either does not compile or throws at XAML load.
    /// </summary>
    [Fact]
    public void TheTypePartition_RejectsATokenOfTheWrongClrType()
    {
        var byProp = AllowedByProperty();

        // FontSize wants a double. A FontWeight, a FontFamily and the raw family string are all
        // "Font"-named and were all pooled into it before.
        Reject("FontSize", "{StaticResource FontWeightStrong}");
        Reject("FontSize", "Tokens.FontWeightMedium");
        Reject("FontSize", "Tokens.FontStandard");
        Reject("FontSize", "Tokens.FontFamilyStandard");

        // FontWeight wants a FontWeight. A double there does not compile in C#; the family string
        // is the reviewer's second example and throws at load.
        Reject("FontWeight", "{StaticResource FontFamilyStandard}");
        Reject("FontWeight", "Tokens.FontSizeBody");
        Reject("FontWeight", "Tokens.FontStandard");

        // FontFamily wants a FontFamily (or the string the XAML converter accepts).
        Reject("FontFamily", "Tokens.FontSizeTitle");
        Reject("FontFamily", "{StaticResource FontWeightStrong}");

        // CornerRadius wants a CornerRadius, or one of the four radius doubles. Nothing from the
        // typography role, whatever its type.
        Reject("CornerRadius", "Tokens.FontSizeDisplay");
        Reject("CornerRadius", "{StaticResource FontStandard}");

        // The name/type collision the partition cannot resolve by type alone: FontSize and the
        // four raw CornerRadius keys are all doubles, so only the role separates them. Both
        // directions are pinned, because a partition rebuilt on type alone breaks exactly here.
        Reject("FontSize", "Tokens.CornerRadiusCircle");
        Reject("CornerRadius", "Tokens.FontSizeBody");

        // Substring matching: a type that merely ENDS in an allowed reference is not that
        // reference. Nothing in the tree is named this today — the point is that the whitelist
        // must not accept a name it never enrolled.
        Reject("FontSize", "LegacyTokens.FontSizeBody");
        Reject("CornerRadius", "OtherTokens.StandardCornerRadius");
        Reject("FontSize", "MyContainerRatio.Small");

        void Reject(string property, string value) =>
            Check.WithCustomMessage(
                    $"`{property} = {value}` satisfies the allow-list. It is the wrong CLR type "
                    + "for that property (or not the reference it resembles), so it is either a "
                    + "compile error or a XamlParseException at load — in front of a user whose "
                    + "only input channel is an eye tracker.")
                .That(IsTokenBacked(value, byProp[property])).IsFalse();
    }

    /// <summary>
    /// The value capture, pinned at the three shapes that pull against each other: a literal in a
    /// later call argument (must flag), a neighbouring assignment (must NOT vouch), and the
    /// spec-prescribed wrapped <c>CornerRadius</c> (must NOT flag). Getting any one of these
    /// wrong is what every previous draft of <see cref="Assignment"/> did.
    /// </summary>
    [Fact]
    public void TheValueCapture_ReadsWholeArgumentLists_ButStopsAtTheNextAssignment()
    {
        // The hole this closed. Before the capture balanced parentheses it stopped at the first
        // comma, so the token in argument one cleared whatever argument two held.
        Check.WithCustomMessage(
                "A raw literal in a later call argument is being cleared by a token in an earlier "
                + "one. This is live: the four clamped glyph sites all write "
                + "Math.Max(<size> * ContainerRatio.X, Legibility.MinimumGlyphFontSize), and a "
                + "bare 12 written there instead of the const must not pass.")
            .That(Flags("FontSize = Math.Max(Tokens.FontSizeBody, 99),")).IsTrue();
        Check.WithCustomMessage("The argument order should not matter.")
            .That(Flags("FontSize = Math.Max(99, Tokens.FontSizeBody),")).IsTrue();

        // The real site the shape above imitates must still pass.
        Check.WithCustomMessage(
                "A correctly-migrated clamped glyph site is now flagged. Both arguments are "
                + "named, so there is nothing to report.")
            .That(Flags(
                "FontSize = Math.Max(_keySize * ContainerRatio.Small, "
                + "Legibility.MinimumGlyphFontSize),")).IsFalse();

        // The hole NOT reopened. A wider capture that ignored nesting depth would let the
        // neighbouring assignment's token clear this one — measured at 0 of 6 real literals found
        // in one file, back when an earlier draft scanned a fixed window instead.
        Check.WithCustomMessage(
                "A neighbouring assignment's token is vouching for this one again. The capture "
                + "must stop at a comma at nesting depth ZERO; only commas inside a parenthesised "
                + "argument list belong to the value.")
            .That(Flags("FontSize = someLocal, FontFamily = Tokens.FontStandard,")).IsTrue();
        Check.WithCustomMessage("A bare literal beside a token-backed neighbour must still flag.")
            .That(Flags("FontSize = 12, FontFamily = Tokens.FontStandard,")).IsTrue();

        // The form the parent spec prescribes for the note title bar, wrapped exactly as it is
        // written there. Flagging this would be a false positive at a site the plan mandates.
        Check.WithCustomMessage(
                "The spec-prescribed asymmetric CornerRadius is being flagged on its trailing "
                + "zeroes. BareNumber exempts a standalone 0 for precisely this form.")
            .That(Flags(
                "CornerRadius = new CornerRadius(\n    Tokens.CornerRadiusStandard,"
                + "\n    Tokens.CornerRadiusStandard, 0, 0),")).IsFalse();

        // ...and the price of that exemption, recorded rather than hidden. A zero alongside a
        // token passes. It is the narrowest form of the hole that was closed above, and zero is
        // not a magic size: this fixture exists so the cost is a decision on record instead of a
        // surprise.
        Check.WithCustomMessage(
                "Math.Max(token, 0) is now flagged. That is stricter than intended, and it means "
                + "the standalone-zero exemption has changed shape — re-check the wrapped "
                + "CornerRadius fixture above, which depends on it.")
            .That(Flags("FontSize = Math.Max(Tokens.FontSizeBody, 0),")).IsFalse();
        Check.WithCustomMessage("The zero exemption must not extend to 0.5, 07 or 0x20.")
            .That(Flags("FontSize = Math.Max(Tokens.FontSizeBody, 0.5),")).IsTrue();

        // A value that is nothing but a zero references no token, so the first half of IsOffender
        // still catches it — the exemption above never makes a bare value legal on its own.
        Check.WithCustomMessage("A bare 0 as the whole value must still be an offender.")
            .That(Flags("FontSize = 0,")).IsTrue();
    }

    /// <summary>
    /// Runs the real guard over a snippet: true if any assignment in it is reported.
    /// </summary>
    /// <param name="snippet">A fragment of source as it would appear after comment stripping.</param>
    /// <returns>True if the guard flags it.</returns>
    private static bool Flags(string snippet)
    {
        var allowed = AllowedByProperty();
        return Assignment.Matches(snippet).Any(m => IsOffender(m, allowed));
    }
}
