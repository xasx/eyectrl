namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// T14 Phase 2's key-caption gates, measured on the REAL <see cref="KeyboardWindow"/> (built,
/// laid out, never shown — no HWND, no <c>Show()</c>, no input):
///
/// <list type="number">
/// <item><b>No clipping</b> — every caption's advance fits its key box at all 16 allowed
/// <c>KeySizeMultiplier</c> steps (0.5..2.0 by 0.1), with the symbol rule applied, in ALL THREE
/// shipped layouts (qwerty, azerty, qwertz). Also pins the box itself against the ticket's
/// formula, so a wrong key width fails even though the fit limit hides its effect.</item>
/// <item><b>The rule</b> — a symbol caption is drawn at the x1.4 fit-limited size in the theme's
/// medium weight; every other caption keeps today's clamped font and Normal weight (qwertz, the
/// layout T14 picks for).</item>
/// <item><b>Faces</b> — every caption renders with no tofu and every glyph run resolves to the
/// theme face, Segoe UI Symbol or Segoe UI Emoji, in all three shipped layouts (the rule
/// enlarges qwerty's and azerty's arrows and emoji key too).</item>
/// <item><b>Accessible names</b> — a key with a layout <c>accessibleName</c> publishes it and
/// nothing else; a key without one carries no static name, so its UIA name follows the live
/// label (ß announces ẞ under Caps). The AutomationId stays <c>Keyboard_Key_{id}</c>.</item>
/// </list>
///
/// <para>
/// Both the classification and the expected font size are transcribed HERE from the reference rule
/// in the T14 Phase-2 hand-off (PHASE2-README.md sections 1-2) — deliberately not by calling the
/// production helper, so a wrong scale, a missing fit limit, a dropped SemiBold, or a misclassified
/// caption fails these tests rather than agreeing with itself. The constants (1.4, 2 DIP, 2.0 DPI)
/// and the expected caption set are the measured picks, restated here as the oracle.
/// </para>
///
/// <para>
/// The whole class is STA and lives in the WpfApplication collection: it constructs windows, and
/// the collection serialises that against the shared <see cref="Application"/> singleton.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class SymbolCaptionFitTests
{
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 600;
    private const int CooldownMs = 300;

    /// <summary>The KeyboardSettings slider: 0.5 to 2.0 in steps of 0.1 (KeyboardPageSource.cs:22).</summary>
    private static readonly double[] Mults =
        Enumerable.Range(0, 16).Select(i => Math.Round(0.5 + 0.1 * i, 1)).ToArray();

    /// <summary>The three shipped layouts gates 1 and 3 sweep (the symbol rule is layout-agnostic).</summary>
    private static readonly string[] ShippedLayoutFiles = ["qwerty.json", "azerty.json", "qwertz.json"];

    private static readonly string[] AllowedFamilies =
        ["Segoe UI Variable", "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji"];

    /// <summary>
    /// The qwertz symbol captions after round 2 (A2: Caps ⬆; B4: word-backspace ⌫ over "Wort",
    /// so ⌫⌫ is gone and ⌫ is shared by two actions) — the labels the T14 rule must cover.
    /// </summary>
    private static readonly string[] ExpectedSymbolCaptions =
    [
        "\u2B06", "\u21E7", "\u232B", "\u2334", "\u21B5", "\u21E5",
        "\u2702", "\u21B6", "\u21B7", "\u2190", "\u2191", "\u2192", "\u2193", "\uD83D\uDE00",
    ];

    /// <summary>
    /// The only (layout, specialAction, KeySizeMultiplier) cases allowed to overflow their key box.
    /// Both are the SAME pre-existing clip: "Add Word" (1.4u) at the smallest allowed key size,
    /// before T14 — the caption T14 renamed to "+ Wort" on qwertz for exactly this reason, left
    /// alone on the English layouts the ticket marks out of scope. Keyed by action and size, not by
    /// caption, and the freshness check in gate 1 fails if either entry stops clipping, so fixing
    /// the English layouts forces the entries out rather than leaving them protecting nothing.
    /// </summary>
    private static readonly HashSet<(string File, string Action, double Mult)> PreExistingClips =
    [
        ("qwerty.json", "addWord", 0.5),
        ("azerty.json", "addWord", 0.5),
    ];

    private readonly ITestOutputHelper _output;

    public SymbolCaptionFitTests(ITestOutputHelper output) => _output = output;

    private static KeyboardLayout ShippedLayout(string file) => KeyboardLayoutLoader.Load(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", file));

    private static KeyboardLayout ShippedQwertz() => ShippedLayout("qwertz.json");

    // ---- gate 1: no caption clips, at any allowed key size ----

    [StaTheory]
    [InlineData("qwerty.json")]
    [InlineData("azerty.json")]
    [InlineData("qwertz.json")]
    public void ShippedLayout_NoCaptionClips_AtEveryAllowedKeySize(string file)
    {
        var layout = ShippedLayout(file);
        var (window, vm) = Open(layout, Mults[0]);

        var clipped = new List<string>();
        var clippedExceptions = new HashSet<(string File, string Action, double Mult)>();
        var wrongBox = new List<string>();
        var wrongKeySize = new List<string>();
        var notOneLine = new List<string>();
        var linePins = new List<string>();
        var verticalOverflow = new List<string>();
        var cases = new List<(double Side, string Where)>();
        var minAdvance = double.PositiveInfinity;
        try
        {
            Check.WithCustomMessage(
                    "The pre-existing-clip exception list is CLOSED at its two entries (the "
                    + "qwerty/azerty 'Add Word' follow-up). Adding an entry here hides a clip "
                    + "behind a comment — fix the layout instead.")
                 .That(PreExistingClips).HasSize(2);
            Check.WithCustomMessage("an exception names a layout the three-case sweep does not cover")
                 .That(PreExistingClips.All(e => ShippedLayoutFiles.Contains(e.File))).IsTrue();

            foreach (var mult in Mults)
            {
                window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
                foreach (var sub in layout.SubLayouts.Keys)
                {
                    vm.SwitchSubLayout(sub);
                    Layout(window);

                    foreach (var (id, key, container, label, content) in KeyVisuals(window, layout, sub))
                    {
                        Check.WithCustomMessage($"{file} {sub}:{id} ('{label.Text}') was not arranged at {mult:0.0}x")
                             .That(container.ActualWidth).IsStrictlyGreaterThan(0.0);

                        // F1: the box is the ticket's formula — keySize x units + (units - 1) x 4
                        // for units > 1 — restated here and computed from the key's own laid-out
                        // height, NOT read back from the window's boxWidth. Dropping the gap (R9)
                        // narrows every multi-unit key, moves its dwell target, and dies here.
                        var expectedWidth = container.ActualHeight * key.Width
                            + (key.Width > 1 ? (key.Width - 1) * 4 : 0);
                        if (Math.Abs(container.ActualWidth - expectedWidth) > 1e-4)
                            wrongBox.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: box "
                                         + $"{container.ActualWidth:0.###}, formula {expectedWidth:0.###}");

                        // Clipping is the ADVANCE (what the text needs) exceeding the box. Not
                        // DesiredSize: when a label overflows, layout clamps DesiredSize to the
                        // available width, so the overflow is invisible there. A two-line caption
                        // (B4) is as wide as its WIDEST line, so both lines are checked.
                        var advance = CaptionLines(content, label).Max(Advance);
                        minAdvance = Math.Min(minAdvance, advance);
                        var side = container.ActualWidth - advance;
                        cases.Add((side, $"{sub} '{label.Text}' ({id}) at {mult:0.0}x"));
                        if (advance > container.ActualWidth + 1e-6)
                        {
                            var exception = (file, key.SpecialAction ?? "", mult);
                            if (PreExistingClips.Contains(exception))
                                clippedExceptions.Add(exception);
                            else
                                clipped.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: advance "
                                            + $"{advance:0.000} > box {container.ActualWidth:0.000}");
                        }

                        // The round-2 vertical gate. Measured UNCONSTRAINED and against the key
                        // height minus the rule's edge padding: inside the fixed-height Border WPF
                        // clamps DesiredSize to the available height, so reading it there can never
                        // fail (review-03 F1; mutant V1 survived that). Invalidate first — a clean
                        // element ignores a new measure constraint. The designer measured the
                        // ⌫/Wort stack at 28.8 DIP in 35 at 0.5x.
                        content.InvalidateMeasure();
                        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var captionHeight = content.DesiredSize.Height;
                        if (captionHeight > container.ActualHeight - 2 * 2.0 + 1e-6)
                            verticalOverflow.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: "
                                                 + $"caption {captionHeight:0.###} > key "
                                                 + $"{container.ActualHeight - 2 * 2.0:0.###} minus padding");

                        // A two-line caption is exactly as tall as its two font sizes by
                        // construction: each line is BlockLineHeight with LineHeight == FontSize
                        // (V1 removes those and must fail), and the height cap guarantees their sum
                        // keeps the edge padding. Pinned per line so a failure names the line.
                        if (key.SubLabel is not null)
                        {
                            var lines = CaptionLines(content, label).ToList();
                            foreach (var line in lines)
                            {
                                if (line.LineStackingStrategy != LineStackingStrategy.BlockLineHeight
                                    || Math.Abs(line.LineHeight - line.FontSize) > 1e-9)
                                    linePins.Add($"{file} {sub} '{line.Text}' ({id}) at {mult:0.0}x: "
                                                 + $"LineStackingStrategy={line.LineStackingStrategy}, "
                                                 + $"LineHeight={line.LineHeight:0.###}, FontSize={line.FontSize:0.###}");
                            }
                            var fontSum = lines.Sum(l => l.FontSize);
                            if (fontSum > container.ActualHeight - 2 * 2.0 + 1e-6)
                                linePins.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: line fonts "
                                             + $"{fontSum:0.###} > key {container.ActualHeight:0.###} minus padding");
                        }

                        // The key's own size is pinned too: this gate's arithmetic is stated in
                        // keySize, and nothing else holds the hidden 70 DIP BaseKeySize (K1b).
                        if (Math.Abs(container.ActualHeight - 70 * mult) > 1e-4)
                            wrongKeySize.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: key height "
                                             + $"{container.ActualHeight:0.###}, expected {70 * mult:0.###}");

                        // The ticket draws the line at "one line, no wrapping or trimming" per
                        // caption line. Pinned because the advance gate would still pass if a
                        // future edit enabled wrapping: the unwrapped advance is what it measures.
                        foreach (var line in CaptionLines(content, label))
                            if (line.TextWrapping != TextWrapping.NoWrap || line.TextTrimming != TextTrimming.None)
                                notOneLine.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: "
                                               + $"TextWrapping={line.TextWrapping}, TextTrimming={line.TextTrimming}");
                    }
                }
            }

            Check.WithCustomMessage("no label advance was measured — the sweep measured nothing")
                 .That(minAdvance).IsStrictlyGreaterThan(0.0);
            Check.WithCustomMessage(
                    "Key boxes do not match the ticket's formula keySize x units + (units - 1) x 4: "
                    + string.Join("; ", wrongBox))
                 .That(wrongBox).IsEmpty();
            Check.WithCustomMessage(
                    "Captions overflow their key box (T14's binding gate): "
                    + string.Join("; ", clipped))
                 .That(clipped).IsEmpty();
            Check.WithCustomMessage(
                    "Captions overflow their key's height (the round-2 two-line gate): "
                    + string.Join("; ", verticalOverflow))
                 .That(verticalOverflow).IsEmpty();
            Check.WithCustomMessage(
                    "Two-line caption lines are not exactly their font size high: "
                    + string.Join("; ", linePins))
                 .That(linePins).IsEmpty();
            Check.WithCustomMessage(
                    "Key heights do not match BaseKeySize (70) x multiplier: "
                    + string.Join("; ", wrongKeySize))
                 .That(wrongKeySize).IsEmpty();

            // Freshness: an exception that stopped clipping is protecting nothing and would
            // happily hide the same caption clipping again later. Fixing the English layouts must
            // delete its entry here. Scoped to THIS case's layout: a case only sweeps its own file.
            var staleExceptions = PreExistingClips.Where(e => e.File == file).Except(clippedExceptions)
                .Select(e => $"{e.File} '{e.Action}' at {e.Mult:0.0}x no longer clips — drop its entry")
                .ToList();
            Check.WithCustomMessage(
                    "Pre-existing-clip exceptions that no longer clip: " + string.Join("; ", staleExceptions))
                 .That(staleExceptions).IsEmpty();
            Check.WithCustomMessage(
                    "Captions may wrap or trim (T14 requires one line, unwrapped, untrimmed): "
                    + string.Join("; ", notOneLine))
                 .That(notOneLine).IsEmpty();

            _output.WriteLine($"{file}: {cases.Count} key x size cases; no new clipping "
                + $"({clippedExceptions.Count} of {PreExistingClips.Count} pre-existing exception "
                + "cases seen). Tightest advances: "
                + string.Join(" | ", cases.OrderBy(c => c.Side).Take(6)
                    .Select(c => $"{c.Where}: {c.Side:+0.000}")));
        }
        finally { window.Close(); }
    }

    // ---- gate 2: the rule applied — x1.4 SemiBold for symbol captions, today's font otherwise ----

    [StaFact]
    public void ShippedQwertz_SymbolCaptions_AreScaledAndSemiBold_EverythingElseUntouched()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, Mults[0]);

        var symbolCases = 0;
        var symbolLabels = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        try
        {
            foreach (var mult in Mults)
            {
                window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
                foreach (var sub in layout.SubLayouts.Keys)
                {
                    vm.SwitchSubLayout(sub);
                    Layout(window);

                    foreach (var (id, key, container, label, content) in KeyVisuals(window, layout, sub))
                    {
                        var symbol = IsSymbolCaptionOracle(key);
                        var baseFont = Math.Max(
                            container.ActualHeight * ContainerRatio.Small,
                            Legibility.MinimumGlyphFontSize);
                        var expectedFont = symbol
                            ? OracleSymbolFont(label.Text, baseFont, container.ActualWidth)
                            : baseFont;
                        if (symbol && key.SubLabel is not null)
                        {
                            // Round-2 B4: the symbol line is also capped so the two-line stack
                            // fits the key's height.
                            expectedFont = Math.Min(
                                expectedFont,
                                container.ActualHeight - 2 * 2.0 - OracleSubLabelFont(baseFont));
                        }
                        var expectedWeight = symbol ? Tokens.FontWeightMedium : FontWeights.Normal;

                        if (Math.Abs(label.FontSize - expectedFont) > 1e-4)
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: FontSize "
                                         + $"{label.FontSize:0.###}, expected {expectedFont:0.###}"
                                         + (symbol ? "" : " (ordinary caption)"));
                        if (label.FontWeight != expectedWeight)
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: FontWeight "
                                         + $"{label.FontWeight}, expected {expectedWeight}"
                                         + (symbol ? " (symbol caption)" : " (ordinary caption)"));
                        if (symbol) { symbolCases++; symbolLabels.Add(label.Text); }

                        // B4's second line: the sub-label at max(0.75 x base, 12), Normal.
                        if (key.SubLabel is null) continue;
                        var wordLine = CaptionLines(content, label)
                            .SingleOrDefault(t => !ReferenceEquals(t, label));
                        if (wordLine is null)
                        {
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: SubLabel "
                                         + $"'{key.SubLabel}' has no second caption line");
                            continue;
                        }
                        var expectedWordFont = OracleSubLabelFont(baseFont);
                        if (wordLine.Text != key.SubLabel)
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: second line "
                                         + $"'{wordLine.Text}', expected '{key.SubLabel}'");
                        if (Math.Abs(wordLine.FontSize - expectedWordFont) > 1e-4)
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: sub-label FontSize "
                                         + $"{wordLine.FontSize:0.###}, expected {expectedWordFont:0.###}");
                        if (wordLine.FontWeight != FontWeights.Normal)
                            problems.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: sub-label FontWeight "
                                         + $"{wordLine.FontWeight}, expected Normal");
                    }
                }
            }

            Check.WithCustomMessage("no symbol captions were seen — the sweep measured nothing")
                 .That(symbolCases).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage(
                    "The swept symbol captions are not the final set's: ["
                    + string.Join(" ", symbolLabels.OrderBy(l => l, StringComparer.Ordinal))
                    + "] — a misclassified caption (e.g. '#+=' or a Bild↑) changes this set.")
                 .That(symbolLabels).IsEquivalentTo(ExpectedSymbolCaptions);
            Check.WithCustomMessage(
                    "The x1.4 SemiBold symbol rule is wrong somewhere: " + string.Join("; ", problems))
                 .That(problems).IsEmpty();

            _output.WriteLine($"{symbolCases} symbol-caption cases at x1.4 SemiBold (fit-limited "
                + "where the oracle says so); every ordinary caption at the clamped font, Normal.");
        }
        finally { window.Close(); }
    }

    // ---- gate 3: no tofu, and every glyph run from an allowed face ----

    [StaTheory]
    [InlineData("qwerty.json")]
    [InlineData("azerty.json")]
    [InlineData("qwertz.json")]
    public void ShippedLayout_EveryCaption_RendersWithAnAllowedFace_AndNoTofu(string file)
    {
        var layout = ShippedLayout(file);
        var (window, vm) = Open(layout, 1.5);

        var problems = new List<string>();
        var runCount = 0;
        try
        {
            foreach (var sub in layout.SubLayouts.Keys)
            {
                vm.SwitchSubLayout(sub);
                Layout(window);

                foreach (var (id, _, _, label, content) in KeyVisuals(window, layout, sub))
                {
                    // A B4 key draws two lines; both are checked (the symbol face and the theme
                    // face for "Wort").
                    foreach (var line in CaptionLines(content, label))
                    {
                        var runs = Runs(line);
                        if (runs.Count == 0)
                        {
                            problems.Add($"{file} {sub} '{line.Text}' ({id}): no glyph runs");
                            continue;
                        }
                        runCount += runs.Count;
                        if (runs.Any(r => r.Glyphs.Contains((ushort)0)))
                            problems.Add($"{file} {sub} '{line.Text}' ({id}): TOFU (glyph index 0)");
                        var bad = runs.Select(r => r.Family).Distinct()
                            .Where(f => !AllowedFamilies.Contains(f)).ToList();
                        if (bad.Count > 0)
                            problems.Add($"{file} {sub} '{line.Text}' ({id}): face(s) {string.Join(", ", bad)}");
                    }
                }
            }

            Check.WithCustomMessage("no glyph runs were read — the sweep measured nothing")
                 .That(runCount).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("Caption faces: " + string.Join("; ", problems))
                 .That(problems).IsEmpty();

            _output.WriteLine($"{file}: {runCount} glyph runs read; every one resolves to "
                + string.Join(", ", AllowedFamilies) + ", none is tofu.");
        }
        finally { window.Close(); }
    }

    // ---- gate 4: accessible names wired onto the real labels ----

    [StaFact]
    public void ShippedQwertz_AccessibleNames_ServeThePicksAndNothingElse()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);

        var named = 0;
        var problems = new List<string>();
        try
        {
            foreach (var sub in layout.SubLayouts.Keys)
            {
                vm.SwitchSubLayout(sub);
                Layout(window);

                foreach (var (id, key, _, label, _) in KeyVisuals(window, layout, sub))
                {
                    var automationId = AutomationProperties.GetAutomationId(label);
                    if (automationId != $"Keyboard_Key_{id}")
                        problems.Add($"{sub} '{label.Text}' ({id}): AutomationId '{automationId}'");

                    var actual = AutomationProperties.GetName(label);
                    if (key.AccessibleName is null)
                    {
                        // F2: no static name may be set on an unnamed key, or its UIA name would
                        // freeze at the resting label while the visible text shifts (ß -> ẞ).
                        if (!string.IsNullOrEmpty(actual))
                            problems.Add($"{sub} '{label.Text}' ({id}): unnamed key carries the "
                                         + $"static UIA name '{actual}'");
                        continue;
                    }

                    named++;
                    if (actual != key.AccessibleName)
                        problems.Add($"{sub} '{label.Text}' ({id}): UIA name '{actual}', "
                                     + $"expected '{key.AccessibleName}'");
                    if (actual == label.Text)
                        problems.Add($"{sub} '{label.Text}' ({id}): a symbol caption still announces its glyph");
                }
            }

            Check.WithCustomMessage("no accessible names were wired — the sweep measured nothing")
                 .That(named).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("UIA names: " + string.Join("; ", problems))
                 .That(problems).IsEmpty();

            _output.WriteLine($"{named} named-key cases; named keys publish exactly their "
                + "accessibleName, unnamed keys publish no static name, and every AutomationId "
                + "is Keyboard_Key_{id}.");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// F2: a key without an <c>accessibleName</c> must announce the label it is showing. The
    /// window may set an explicit UIA name only for the symbol captions the layout names; an
    /// explicit name on a shift-variant key freezes it at the resting label, so under Caps the
    /// user would hear "ß" while seeing ẞ (and "é" while seeing É). The peer's live text is the
    /// fallback the fix restores.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_UnnamedShiftVariantKeys_AnnounceTheirShiftedLabel()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            var sharpS = KeyVisuals(window, layout, "letters").Single(v => v.Key.Output == "ß");
            Check.WithCustomMessage("the ß key picked up an accessibleName — the pin below would not measure the fallback")
                 .That(sharpS.Key.AccessibleName).IsNull();
            Check.That(AutomationProperties.GetName(sharpS.Label)).IsEmpty();
            Check.That(PeerName(sharpS.Label)).IsEqualTo("ß");

            vm.ToggleModifier(ModifierKey.CapsLock);
            Check.WithCustomMessage("the ß key did not relabel under Caps — the announcement assertion would be vacuous")
                 .That(sharpS.Label.Text).IsEqualTo("ẞ");
            Check.WithCustomMessage(
                    "A key without an accessibleName is announced by its static resting label under "
                    + "Caps: an explicit SetName froze it. The peer must follow the live text.")
                 .That(PeerName(sharpS.Label)).IsEqualTo("ẞ");

            vm.SwitchSubLayout("accents");
            Layout(window);
            var accented = KeyVisuals(window, layout, "accents").Single(v => v.Key.Output == "é");
            Check.That(accented.Key.AccessibleName).IsNull();
            Check.That(AutomationProperties.GetName(accented.Label)).IsEmpty();
            Check.WithCustomMessage("an accent key with no accessibleName does not announce its "
                    + "capital under Caps")
                 .That(PeerName(accented.Label)).IsEqualTo("É");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// B4's second line must survive every repaint. The window rebuilds captions on sub-layout
    /// swaps and erases them with modifier repaints; a repaint that rebuilt the key's content from
    /// the label alone would silently drop "Wort" while the dwell outline, the id and the name
    /// stayed healthy. Caps and Shift are both exercised, twice so a repaint that drops the stack
    /// on the way back is caught too.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_TwoLineCaption_SurvivesCapsAndShiftRepaints()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var modifier in new[] { ModifierKey.CapsLock, ModifierKey.Shift })
            {
                var before = TwoLine(window, layout);
                Check.WithCustomMessage($"{modifier}: the word-backspace key is not a two-line caption to begin with")
                     .That(before).IsNotNull();

                vm.ToggleModifier(modifier);
                Layout(window);
                var on = TwoLine(window, layout);
                Check.WithCustomMessage($"{modifier} on dropped the sub-label (content "
                        + $"{ContentKind(window, layout)}): the two-line caption must survive every repaint.")
                     .That(on).IsNotNull();
                Check.That(on!.Value.Symbol.Text).IsEqualTo("⌫");
                Check.That(on.Value.Symbol.FontSize).IsEqualTo(before!.Value.Symbol.FontSize);
                Check.That(on.Value.Word.Text).IsEqualTo("Wort");
                Check.That(on.Value.Word.FontSize).IsEqualTo(before.Value.Word.FontSize);

                vm.ToggleModifier(modifier);
                Layout(window);
                Check.WithCustomMessage($"{modifier} off dropped the sub-label")
                     .That(TwoLine(window, layout)).IsNotNull();
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The B4 height cap, pinned where it actually binds. At the shipped sizes the cap never
    /// engages (35 - 4 - 12 = 19 ≥ 16.8 at 0.5x), so a synthetic base font large enough for the
    /// two lines to overrun a 35 DIP key is the only way to see it: dropping the cap returns the
    /// uncapped x1.4 font here.
    /// </summary>
    [StaFact]
    public void SymbolLine_IsHeightCappedSoTheTwoLineStackFitsTheKey()
    {
        const double keySize = 35.0;
        const double baseFont = 40.0;
        const double edgePadding = 2.0;
        var key = new KeyDefinition
        {
            Label = "⌫", SubLabel = "Wort", SpecialAction = "deleteWord", Width = 1.2
        };

        var symbolFont = SymbolCaptionFonts.Resolve(key, baseFont, boxWidth: 42.8, keySize);
        var wordFont = SymbolCaptionFonts.SubLabelFont(baseFont);

        Check.WithCustomMessage(
                $"symbol {symbolFont:0.###} + word {wordFont:0.###} must fit {keySize} - "
                + $"2 x {edgePadding} = {keySize - 2 * edgePadding:0.###}")
             .That(symbolFont + wordFont).IsLessOrEqualThan(keySize - 2 * edgePadding);
        Check.WithCustomMessage(
                "the cap did not bind, so this fixture no longer proves the cap exists — retune it "
                + "(the uncapped x1.4 font must exceed the stack room)")
             .That(baseFont * 1.4).IsStrictlyGreaterThan(keySize - 2 * edgePadding - wordFont);
        Check.WithCustomMessage("the symbol line must be exactly the capped value")
             .That(symbolFont).IsEqualTo(keySize - 2 * edgePadding - wordFont);
    }

    private static (TextBlock Symbol, TextBlock Word)? TwoLine(KeyboardWindow window, KeyboardLayout layout)
    {
        var visual = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == "deleteWord");
        if (visual.Content is not StackPanel stack) return null;
        var lines = stack.Children.OfType<TextBlock>().ToList();
        return lines.Count == 2 ? (lines[0], lines[1]) : null;
    }

    /// <summary>
    /// F2 (review-03): the B4 word line must not be a second UIA element. A screen reader on the
    /// word-backspace key must hear its one name, "Wort löschen" with Keyboard_Key_deleteWord, and
    /// not "Wort löschen, Wort". The key's peer tree is walked the way UIA would build it: create a
    /// peer for every visual, keep the named ones. DecorativeTextBlock yields no peer at all.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_WordBackspaceKey_ExposesExactlyOneNamedUiaElement()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            var key = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == "deleteWord");
            var named = new List<(string Name, string Id)>();
            void Walk(DependencyObject node)
            {
                if (node is UIElement element
                    && UIElementAutomationPeer.CreatePeerForElement(element) is { } peer
                    && !string.IsNullOrEmpty(peer.GetName()))
                    named.Add((peer.GetName(), peer.GetAutomationId()));
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    Walk(VisualTreeHelper.GetChild(node, i));
            }
            Walk(key.Container);

            Check.WithCustomMessage(
                    "UIA elements under the word-backspace key: "
                    + string.Join("; ", named.Select(n => $"'{n.Name}' id='{n.Id}'")))
                 .That(named).HasSize(1);
            Check.That(named[0].Name).IsEqualTo("Wort löschen");
            Check.That(named[0].Id).IsEqualTo("Keyboard_Key_deleteWord");
        }
        finally { window.Close(); }
    }

    private static string ContentKind(KeyboardWindow window, KeyboardLayout layout)
    {
        var visual = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == "deleteWord");
        return visual.Content.GetType().Name;
    }

    /// <summary>The name UIA would actually announce: the explicit name when set, else the live text.</summary>
    private static string PeerName(TextBlock label) => new TextBlockAutomationPeer(label).GetName();

    // ---- the window / geometry helpers ----

    private static (KeyboardWindow Window, KeyboardViewModel Vm) Open(KeyboardLayout layout, double mult)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
        return (window, vm);
    }

    /// <summary>Replicates the real window's <c>SizeToContent="WidthAndHeight"</c> layout pass.</summary>
    private static void Layout(KeyboardWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
    }

    /// <summary>
    /// The keys <c>RebuildKeys</c> registered for one sub-layout, with their definition: the id
    /// rule is SpecialAction else <c>key_{row}_{col}</c> (KeyboardWindow.xaml.cs). Read through the
    /// window's private <c>_keyVisuals</c> registry, which is the only way to reach the real
    /// containers and labels. <c>Content</c> is the element the dwell outline wraps — the label
    /// itself, or the two-line caption stack whose first line is the label (B4).
    /// </summary>
    private static List<(string Id, KeyDefinition Key, Border Container, TextBlock Label, FrameworkElement Content)>
        KeyVisuals(KeyboardWindow window, KeyboardLayout layout, string sub)
    {
        var byId = new Dictionary<string, KeyDefinition>(StringComparer.Ordinal);
        var rows = layout.SubLayouts[sub].Rows;
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                byId[!string.IsNullOrEmpty(rows[r][c].SpecialAction)
                    ? rows[r][c].SpecialAction!
                    : $"key_{r}_{c}"] = rows[r][c];

        var field = typeof(KeyboardWindow).GetField(
            "_keyVisuals", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var visuals = (IDictionary)field.GetValue(window)!;

        var result = new List<(string, KeyDefinition, Border, TextBlock, FrameworkElement)>();
        foreach (DictionaryEntry entry in visuals)
        {
            var value = entry.Value!;
            var container = (Border)value.GetType().GetField("Item1")!.GetValue(value)!;
            var label = (TextBlock)value.GetType().GetField("Item2")!.GetValue(value)!;
            var id = (string)entry.Key;
            var content = label.Parent is StackPanel stack ? (FrameworkElement)stack : label;
            result.Add((id, byId[id], container, label, content));
        }
        return result;
    }

    /// <summary>The TextBlocks a key draws — one for an ordinary caption, two for a B4 stack.</summary>
    private static IEnumerable<TextBlock> CaptionLines(FrameworkElement content, TextBlock label) =>
        content is StackPanel stack ? stack.Children.OfType<TextBlock>() : [label];

    /// <summary>The label's real advance in DIP, at the size and weight it is drawn with.</summary>
    private static double Advance(TextBlock label) =>
        new FormattedText(label.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip)
            .WidthIncludingTrailingWhitespace;

    /// <summary>
    /// The reference classification (PHASE2-README.md section 2), transcribed into the test as an
    /// independent oracle rather than calling <c>SymbolCaptionRule.IsSymbolCaption</c>: a mutation
    /// that over-classifies a caption must fail HERE, and an oracle that asks production cannot
    /// see it. Must agree with the Core table in SymbolCaptionRuleTests.
    /// </summary>
    private static bool IsSymbolCaptionOracle(KeyDefinition key)
    {
        if (string.IsNullOrEmpty(key.SpecialAction) || string.IsNullOrEmpty(key.Label)) return false;
        var elements = StringInfo.GetTextElementEnumerator(key.Label);
        while (elements.MoveNext())
        {
            var codePoint = char.ConvertToUtf32((string)elements.Current, 0);
            if (codePoint <= 0x7F) return false;
            var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            if (category is not (UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol)) return false;
        }
        return true;
    }

    /// <summary>
    /// The reference implementation of the rule's font (PHASE2-README.md section 2), transcribed
    /// into the test as an independent oracle: the base font times 1.4, reduced just enough that
    /// the SemiBold advance keeps 2 DIP inside each side of the box. Both constants are the
    /// measured picks, not reads of the production ones.
    /// </summary>
    private static double OracleSymbolFont(string label, double baseFont, double boxWidth)
    {
        var font = baseFont * 1.4;
        var advance = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Tokens.FontStandard, FontStyles.Normal, Tokens.FontWeightMedium, FontStretches.Normal),
                font, Brushes.Black, 2.0)
            .WidthIncludingTrailingWhitespace;
        var room = boxWidth - 2 * 2.0;
        return advance > room ? font * room / advance : font;
    }

    /// <summary>
    /// The reference sub-label font (round-2 README, B4): max(0.75 x base, 12), Normal. The 0.75 is
    /// restated here as the oracle, not read from the production constant.
    /// </summary>
    private static double OracleSubLabelFont(double baseFont) =>
        Math.Max(baseFont * 0.75, Legibility.MinimumGlyphFontSize);

    private sealed record GlyphRunInfo(string Family, ushort[] Glyphs);

    /// <summary>
    /// The glyph runs a TextBlock draws, read back from its drawing (the designer's probe routine):
    /// the family each run resolved to and its glyph indices. Index 0 is .notdef — tofu.
    /// </summary>
    private static List<GlyphRunInfo> Runs(TextBlock source)
    {
        var probe = new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            FontStretch = source.FontStretch,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));

        var runs = new List<GlyphRunInfo>();
        void Walk(Drawing? drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case GlyphRunDrawing glyphRunDrawing:
                    var run = glyphRunDrawing.GlyphRun;
                    var typeface = run.GlyphTypeface;
                    var family = typeface.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-us"), out var f)
                        ? f
                        : typeface.FamilyNames.Values.FirstOrDefault() ?? "?";
                    runs.Add(new GlyphRunInfo(family, run.GlyphIndices.ToArray()));
                    break;
            }
        }
        Walk(VisualTreeHelper.GetDrawing(probe));
        return runs;
    }
}
