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
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
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
    /// The qwertz symbol captions after rounds 2-3 (A2: Caps ⬆; C3+D3: word-backspace "Aa" inside
    /// the erase arrow, which is an ordinary caption and contributes nothing here) — the labels
    /// the T14 symbol rule must cover.
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

    private static KeyboardLayout ShippedLayout(string file) => KeyboardLayoutLoader.Load(
        System.IO.Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", file));

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
        var captionPins = new List<string>();
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

                        // Clipping is the ADVANCE (what the caption DRAWS) exceeding the box, not
                        // DesiredSize: WPF clamps that to the available size, so an overflow is
                        // invisible there. An ordinary caption's advance is its widest line; a
                        // framed caption's is the drawn arrow's extent, stroke included.
                        double advance, captionHeight;
                        if (key.LabelFrame is not null)
                        {
                            // The frame's nominal ink box IS the canvas size in every frame style
                            // (the geometry is inset by half the stroke), so it is the advance and
                            // the height the fit gates measure.
                            var canvas = FrameCanvas(content);
                            advance = canvas.Width;
                            captionHeight = canvas.Height;

                            // The frame must stay inside the key minus the rule's edge padding, and
                            // the label must stay the invisible UIA anchor.
                            if (advance > container.ActualWidth - 2 * 2.0 + 1e-6)
                                clipped.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: frame "
                                            + $"{advance:0.000} > box {container.ActualWidth:0.###} minus padding");
                            if (captionHeight > container.ActualHeight - 2 * 2.0 + 1e-6)
                                verticalOverflow.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: frame "
                                                     + $"{captionHeight:0.000} > key {container.ActualHeight:0.###} minus padding");
                            if (label.Opacity != 0)
                                captionPins.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: the frame's "
                                                + $"label is visible (Opacity {label.Opacity})");

                            // The erase frame's fill is text geometry; it must be drawn at a font in
                            // [12, label.FontSize]. The arrows draw no text, so only the erase frame
                            // takes this pin. The 1e-3 slack absorbs Geometry.Bounds'
                            // outline-approximation noise (measured ~1.3e-6); the mutation it guards
                            // moves the glyphs by DIP.
                            if (key.LabelFrame == EraseLabelFrame.EraseLeft)
                            {
                                var text = FramePaths(canvas)[1];
                                var drawnText = text.Data.Bounds;
                                var atKeyFont = TextGeometry(label.Text, label.FontSize).Bounds;
                                var atFloor = TextGeometry(label.Text, Legibility.MinimumGlyphFontSize).Bounds;
                                if (drawnText.Width > atKeyFont.Width + 1e-3 || drawnText.Width < atFloor.Width - 1e-3
                                    || drawnText.Height > atKeyFont.Height + 1e-3 || drawnText.Height < atFloor.Height - 1e-3)
                                    captionPins.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: drawn text "
                                                    + $"{drawnText.Width:0.###}x{drawnText.Height:0.###} is outside the "
                                                    + $"key-font..floor band {atFloor.Width:0.###}x{atFloor.Height:0.###}"
                                                    + $"..{atKeyFont.Width:0.###}x{atKeyFont.Height:0.###}");
                            }
                        }
                        else
                        {
                            advance = CaptionLines(content, label).Max(Advance);
                            // The vertical gate. Measured UNCONSTRAINED and against the key height
                            // minus the rule's edge padding: inside the fixed-height Border WPF
                            // clamps DesiredSize to the available height, so reading it there can
                            // never fail (review-03 F1; mutant V1 survived that). Invalidate first —
                            // a clean element ignores a new measure constraint.
                            content.InvalidateMeasure();
                            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            captionHeight = content.DesiredSize.Height;
                            if (captionHeight > container.ActualHeight - 2 * 2.0 + 1e-6)
                                verticalOverflow.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: "
                                                     + $"caption {captionHeight:0.###} > key "
                                                     + $"{container.ActualHeight - 2 * 2.0:0.###} minus padding");
                        }

                        minAdvance = Math.Min(minAdvance, advance);
                        var side = container.ActualWidth - advance;
                        cases.Add((side, $"{sub} '{label.Text}' ({id}) at {mult:0.0}x"));
                        if (key.LabelFrame is null && advance > container.ActualWidth + 1e-6)
                        {
                            var exception = (file, key.SpecialAction ?? "", mult);
                            if (PreExistingClips.Contains(exception))
                                clippedExceptions.Add(exception);
                            else
                                clipped.Add($"{file} {sub} '{label.Text}' ({id}) at {mult:0.0}x: advance "
                                            + $"{advance:0.000} > box {container.ActualWidth:0.000}");
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
                    "Caption details are wrong (a visible frame label, or a drawn text size outside "
                    + "the key-font..floor band): " + string.Join("; ", captionPins))
                 .That(captionPins).IsEmpty();
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
    /// The round-3 erase frame must survive every repaint. The window rebuilds captions on
    /// sub-layout swaps and erases them with modifier repaints; a repaint that rebuilt the key's
    /// content from the label alone would drop the arrow. Caps and Shift are both exercised, twice
    /// so a repaint that drops the frame on the way back is caught too, and the drawing must keep
    /// following the label's foreground.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_EraseFrame_SurvivesCapsAndShiftRepaints()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var modifier in new[] { ModifierKey.CapsLock, ModifierKey.Shift })
            {
                var before = EraseFrame(window, layout);
                Check.WithCustomMessage($"{modifier}: the word-backspace key has no erase frame to begin with")
                     .That(before).IsNotNull();

                vm.ToggleModifier(modifier);
                Layout(window);
                var on = EraseFrame(window, layout);
                Check.WithCustomMessage(
                        $"{modifier} on dropped the erase frame (content {ContentKind(window, layout)}): "
                        + "the frame must survive every repaint.")
                     .That(on).IsNotNull();
                Check.That(on!.Value.Canvas.Width).IsEqualTo(before!.Value.Canvas.Width);
                Check.That(on.Value.Canvas.Height).IsEqualTo(before.Value.Canvas.Height);
                Check.That(on.Value.Arrow.Data.Bounds).IsEqualTo(before.Value.Arrow.Data.Bounds);
                Check.That(on.Value.Text.Data.Bounds).IsEqualTo(before.Value.Text.Data.Bounds);
                Check.WithCustomMessage("the drawing must follow the label's foreground")
                     .That(on.Value.Arrow.Stroke).IsSameReferenceAs(on.Value.Label.Foreground);
                Check.That(on.Value.Text.Fill).IsSameReferenceAs(on.Value.Label.Foreground);
                Check.That(on.Value.Label.Opacity).IsEqualTo(0.0);

                vm.ToggleModifier(modifier);
                Layout(window);
                Check.WithCustomMessage($"{modifier} off dropped the erase frame")
                     .That(EraseFrame(window, layout)).IsNotNull();
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The erase frame's fit limit, pinned where it binds. With "Aa" the shipped sizes never
    /// squeeze, so the limit is exercised synthetically: a frame wider than its room must shrink
    /// the gap; a base font too large for the box must shrink toward the 12 px floor, never below.
    /// The fixtures self-calibrate from the unfitted frame, so they cannot drift into vacuity.
    /// </summary>
    [StaFact]
    public void EraseFrame_FitLimit_KeepsTheFrameInsideTheEdgePadding()
    {
        const double big = 1000;   // no height/width pressure from the key itself
        var floor = Legibility.MinimumGlyphFontSize;

        // Gap branch: room just under the natural width at the floor font.
        var natural = EraseLabelFrame.Measure("Aa", floor, big, big);
        var room = natural.Width - 0.1;
        var squeezed = EraseLabelFrame.Measure("Aa", floor, big, room + 2 * SymbolCaptionRule.EdgePadding);
        Check.WithCustomMessage(
                $"the fixture no longer exercises the gap shrink — retune it (natural "
                + $"{natural.Width:0.####}, room {room:0.####}, squeezed {squeezed.Width:0.####}, "
                + $"gap {squeezed.Gap:0.####})")
             .That(squeezed.Gap).IsStrictlyLessThan(EraseLabelFrame.GapRatio);
        Check.WithCustomMessage(
                $"the squeezed frame {squeezed.Width:0.###} must stay inside {room:0.###}")
             .That(squeezed.Width).IsLessOrEqualThan(room + 2e-6);   // the fit's own 1e-6 epsilon + slack

        // The build must emit exactly the measured frame, and its stroked ink must stay inside it.
        var label = new TextBlock { Text = "Aa", FontSize = floor, Foreground = Tokens.BrushTextPrimary };
        var holder = EraseLabelFrame.Build(label, big, room + 2 * SymbolCaptionRule.EdgePadding);
        var canvas = ((Grid)holder).Children.OfType<Canvas>().Single();
        var arrow = canvas.Children.OfType<Path>().First();
        Check.That(canvas.Width).IsEqualTo(squeezed.Width);
        Check.That(canvas.Height).IsEqualTo(squeezed.Height);
        // Ink-consistency: the stroked outline must not paint outside the nominal frame. 1e-3
        // absorbs Geometry.Bounds' outline-approximation noise (measured ~1.6e-7).
        Check.That(arrow.Data.Bounds.Width + arrow.StrokeThickness).IsLessOrEqualThan(canvas.Width + 1e-3);
        Check.That(arrow.Data.Bounds.Height + arrow.StrokeThickness).IsLessOrEqualThan(canvas.Height + 1e-3);

        // Font branch: a room between the floor's and the base font's natural width shrinks the
        // font toward the floor, and the drawn frame respects that room.
        var atFloor = EraseLabelFrame.Measure("AAAA", floor, big, big);
        var atBase = EraseLabelFrame.Measure("AAAA", 24, big, big);
        var midRoom = (atFloor.Width + atBase.Width) / 2;
        var shrunk = EraseLabelFrame.Measure("AAAA", 24, big, midRoom + 2 * SymbolCaptionRule.EdgePadding);
        Check.That(shrunk.Font).IsStrictlyLessThan(24);
        Check.WithCustomMessage("the fit limit shrank the text below the 12 px legibility floor")
             .That(shrunk.Font).IsGreaterOrEqualThan(floor);
        Check.That(shrunk.Width).IsLessOrEqualThan(midRoom + 2e-6);   // the fit's own 1e-6 epsilon + slack
    }

    /// <summary>
    /// F1 (review-05): the erase frame's SHAPE, pinned from the designer's own D3 report
    /// (C:\t\keys\round3b-measurements.txt, D3 "Aa"): the unsqueezed arrow is 53.7 x 34.0 at
    /// 1.5x/1.2u and 26.7 x 16.9 at 0.5x, the text 24.15 / 12 px, the inner gap 0.32. Read through
    /// the real window at both sizes, with the canvas centred on its key. Six geometry mutations
    /// (G1..G6) survived the suite before this test; each must fail it.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_EraseFrame_MatchesTheDesignersGoldenGeometry()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var (mult, expectedW, expectedH, expectedFont) in new[]
                     {
                         (1.5, 53.7, 34.0, 24.15),
                         (0.5, 26.7, 16.9, 12.0),
                     })
            {
                window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
                vm.SwitchSubLayout("letters");
                Layout(window);

                var visual = KeyVisuals(window, layout, "letters")
                    .Single(v => v.Key.SpecialAction == "deleteWord");
                var canvas = FrameCanvas(visual.Content);
                var paths = FramePaths(canvas);
                var arrow = paths[0];
                var text = paths[1];
                var frame = EraseLabelFrame.Measure(visual.Key.Label, visual.Label.FontSize,
                    visual.Container.ActualHeight, visual.Container.ActualWidth);

                Check.WithCustomMessage(
                        $"{mult:0.0}x: arrow {canvas.Width:0.0}x{canvas.Height:0.0} in key "
                        + $"{visual.Container.ActualWidth:0.0}x{visual.Container.ActualHeight:0.0}; the "
                        + $"designer's D3 is {expectedW}x{expectedH} (tolerance 0.05)")
                     .That(canvas.Width).IsCloseTo(expectedW, 0.05);
                Check.That(canvas.Height).IsCloseTo(expectedH, 0.05);
                Check.WithCustomMessage(
                        $"{mult:0.0}x: drawn font {frame.Font:0.00}, the designer's D3 is {expectedFont}")
                     .That(frame.Font).IsCloseTo(expectedFont, 0.05);
                Check.WithCustomMessage($"{mult:0.0}x: inner gap {frame.Gap:0.000}, expected 0.32")
                     .That(frame.Gap).IsCloseTo(0.32, 0.001);

                // The build emits the measured frame, drawn in the label's own foreground ...
                Check.That(canvas.Width).IsEqualTo(frame.Width);
                Check.That(canvas.Height).IsEqualTo(frame.Height);
                Check.That(arrow.Stroke).IsSameReferenceAs(visual.Label.Foreground);
                Check.That(text.Fill).IsSameReferenceAs(visual.Label.Foreground);

                // ... centred on the key, not aligned to an edge (G6).
                var centre = canvas.TranslatePoint(
                    new Point(canvas.Width / 2, canvas.Height / 2), visual.Container);
                Check.WithCustomMessage(
                        $"{mult:0.0}x: canvas centre {centre.X:0.##},{centre.Y:0.##} must be the key "
                        + $"centre {visual.Container.ActualWidth / 2:0.##},{visual.Container.ActualHeight / 2:0.##}")
                     .That(centre.X).IsCloseTo(visual.Container.ActualWidth / 2, 0.01);
                Check.That(centre.Y).IsCloseTo(visual.Container.ActualHeight / 2, 0.01);
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The erase arrow's calibration constants, measured from the real ⌫ glyph and named in the
    /// designer's rounding (review-05 F1: tip 0.948, stroke 0.0863, gap 0.32). Pinned so a style
    /// change to any of the three fails a named test even where the golden fixture is blind.
    /// </summary>
    [Fact]
    public void EraseFrame_CalibrationConstants_AreTheMeasuredOnes()
    {
        Check.That(EraseLabelFrame.TipRatio).IsCloseTo(0.948, 0.001);
        Check.That(EraseLabelFrame.StrokeRatio).IsCloseTo(0.0863, 0.001);
        Check.That(EraseLabelFrame.GapRatio).IsCloseTo(0.32, 0.001);
    }

    /// <summary>
    /// The round-6 wide arrows (G4+, 2026-10-09), pinned from the designer's own G4+ report
    /// (C:\t\keys\round6-measurements.txt): at 1.5x/letters the drawn ink is Caps 42.0 x 57.5,
    /// Shift 42.0 x 46.5, Enter 42.0 x 32.0 (tolerance 0.05, ink measured the designer's way at
    /// 192 dpi); at 0.5x the ink stays one third of the key for Caps and Shift and 0.267 of its
    /// 1.5-unit key for Enter. Caps' fill and the Shift outline are pinned, so "Caps not filled"
    /// dies here too.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_ArrowFrames_MatchTheDesignersGoldenGeometry()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var (mult, golden) in new[]
                     {
                         (1.5, new (string Action, double W, double H, double Frac)[]
                         {
                             ("toggle:capslock", 42.0, 57.5, 1 / 3.0),
                             ("modifier:shift", 42.0, 46.5, 1 / 3.0),
                             ("enter", 42.0, 32.0, 0.2667),
                             ("tab", 42.5, 28.0, 0.41),
                         }),
                         (0.5, new (string Action, double W, double H, double Frac)[]
                         {
                             ("toggle:capslock", 0, 0, 1 / 3.0),
                             ("modifier:shift", 0, 0, 1 / 3.0),
                             ("enter", 0, 0, 0.2667),
                             ("tab", 0, 0, 0.41),
                         }),
                     })
            {
                window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
                vm.SwitchSubLayout("letters");
                Layout(window);
                var root = (FrameworkElement)window.Content;

                foreach (var (action, expectedW, expectedH, expectedFraction) in golden)
                {
                    var visual = KeyVisuals(window, layout, "letters")
                        .Single(v => v.Key.SpecialAction == action);
                    var canvas = FrameCanvas(visual.Content);
                    var path = FramePaths(canvas).Single();
                    var ink = MeasureInk(visual.Container, root);

                    if (mult == 1.5)
                    {
                        Check.WithCustomMessage(
                                $"{action} at 1.5x: ink {ink.Width:0.0}x{ink.Height:0.0}, the designer's "
                                + $"G4+ is {expectedW}x{expectedH} (tolerance 0.05)")
                             .That(ink.Width).IsCloseTo(expectedW, 0.05);
                        Check.That(ink.Height).IsCloseTo(expectedH, 0.05);
                        // The canvas is the nominal target (one third of the box), 0.267 more than
                        // the 192-dpi ink measurement's rounded width.
                        Check.That(canvas.Width)
                             .IsCloseTo(ArrowLabelFrame.TargetWidth(visual.Container.ActualHeight), 1e-9);
                    }
                    Check.WithCustomMessage(
                            $"{action} at {mult:0.0}x: ink {ink.Width:0.0} is "
                            + $"{ink.Width / visual.Container.ActualWidth:P1} of the key, the designer's G4+ is "
                            + $"{expectedFraction:P1}")
                         .That(ink.Width / visual.Container.ActualWidth).IsCloseTo(expectedFraction, 0.01);

                    // The frame is the drawn geometry in the label's own brush; Caps and the Enter
                    // head are filled, the Shift outline is not.
                    Check.That(path.Stroke).IsSameReferenceAs(visual.Label.Foreground);
                    if (action == "modifier:shift")
                        Check.That(path.Fill).IsNull();
                    else
                        Check.That(path.Fill).IsSameReferenceAs(visual.Label.Foreground);
                    Check.That(visual.Label.Opacity).IsEqualTo(0.0);

                    // The stroke is the ⌫ key's own: the measured ratio times the label's symbol
                    // font (2.916 DIP at 1.5x) — S5.
                    Check.WithCustomMessage(
                            $"{action} at {mult:0.0}x: stroke {path.StrokeThickness:0.###} must be "
                            + $"EraseLabelFrame.StrokeRatio x the label font = "
                            + $"{EraseLabelFrame.StrokeRatio * visual.Label.FontSize:0.###}")
                         .That(path.StrokeThickness)
                         .IsCloseTo(EraseLabelFrame.StrokeRatio * visual.Label.FontSize, 1e-9);

                    // The canvas is centred on the key (S3), and the Caps bar on the canvas (S2).
                    var centre = canvas.TranslatePoint(
                        new Point(canvas.Width / 2, canvas.Height / 2), visual.Container);
                    Check.WithCustomMessage(
                            $"{action} at {mult:0.0}x: canvas centre {centre.X:0.##},{centre.Y:0.##} must be the "
                            + $"key centre {visual.Container.ActualWidth / 2:0.##},{visual.Container.ActualHeight / 2:0.##}")
                         .That(centre.X).IsCloseTo(visual.Container.ActualWidth / 2, 0.01);
                    Check.That(centre.Y).IsCloseTo(visual.Container.ActualHeight / 2, 0.01);
                    if (action == "toggle:capslock")
                    {
                        var bar = ((GeometryGroup)path.Data).Children[1].Bounds;
                        Check.WithCustomMessage(
                                $"{action} at {mult:0.0}x: the lock bar centre "
                                + $"{bar.Left + bar.Width / 2:0.###} must be the canvas centre "
                                + $"{canvas.Width / 2:0.###}")
                             .That(bar.Left + bar.Width / 2).IsCloseTo(canvas.Width / 2, 0.01);
                    }
                    if (action == "tab")
                    {
                        // →|: the head points right, and the stop bar sits at w − t/2.
                        var figures = ((PathGeometry)path.Data).Figures;
                        var head = figures[1];
                        var baseX = ((PolyLineSegment)head.Segments[0]).Points[0].X;
                        Check.WithCustomMessage(
                                $"{action} at {mult:0.0}x: the head must point right (tip {head.StartPoint.X:0.###} "
                                + $"must be right of its base {baseX:0.###})")
                             .That(head.StartPoint.X).IsStrictlyGreaterThan(baseX);
                        var bar = figures[2];
                        var barX = ((LineSegment)bar.Segments[0]).Point.X;
                        Check.WithCustomMessage(
                                $"{action} at {mult:0.0}x: the stop bar must sit at w - t/2 = "
                                + $"{canvas.Width - path.StrokeThickness / 2:0.###}, not {barX:0.###}")
                             .That(barX).IsCloseTo(canvas.Width - path.StrokeThickness / 2, 1e-9);
                        Check.That(bar.StartPoint.X).IsCloseTo(canvas.Width - path.StrokeThickness / 2, 1e-9);
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The wide arrows' proportions, as the round-6 spec states them: aspect 1.1, head 55 % of the
    /// inset height, shaft 48 % of the inset width, the Caps bar 0.16w tall and 0.10w below at the
    /// shaft's ink width, and Enter 0.75w tall with a 0.32w head of half-height 0.24w. Pinned so a
    /// proportion change fails a named test.
    /// </summary>
    [Fact]
    public void ArrowFrames_CalibrationConstants_AreTheMeasuredOnes()
    {
        Check.That(ArrowLabelFrame.Aspect).IsEqualTo(1.1);
        Check.That(ArrowLabelFrame.HeadRatio).IsEqualTo(0.55);
        Check.That(ArrowLabelFrame.ShaftRatio).IsEqualTo(0.48);
        Check.That(ArrowLabelFrame.BarRatio).IsEqualTo(0.16);
        Check.That(ArrowLabelFrame.BarGapRatio).IsEqualTo(0.10);
        Check.That(ArrowLabelFrame.EnterAspect).IsEqualTo(0.75);
        Check.That(ArrowLabelFrame.EnterHeadLength).IsEqualTo(0.32);
        Check.That(ArrowLabelFrame.EnterHeadHalf).IsEqualTo(0.24);
        Check.That(ArrowLabelFrame.TabAspect).IsEqualTo(0.66);
        Check.WithCustomMessage("the target is one third of a 1.2-unit box: 42.267 DIP at 1.5x")
             .That(ArrowLabelFrame.TargetWidth(105)).IsCloseTo((105 * 1.2 + 0.8) / 3, 1e-9);
        Check.That(ArrowLabelFrame.IsArrow(ArrowLabelFrame.CapsArrow)).IsTrue();
        Check.That(ArrowLabelFrame.IsArrow(ArrowLabelFrame.ShiftArrow)).IsTrue();
        Check.That(ArrowLabelFrame.IsArrow(ArrowLabelFrame.EnterArrow)).IsTrue();
        Check.That(ArrowLabelFrame.IsArrow(ArrowLabelFrame.TabArrow)).IsTrue();
        Check.That(ArrowLabelFrame.IsArrow(EraseLabelFrame.EraseLeft)).IsFalse();
        Check.That(ArrowLabelFrame.IsArrow(null)).IsFalse();
    }

    /// <summary>
    /// The wide arrows must survive every repaint (Caps on/off, Shift on/off), keep their geometry
    /// and brush, and never leave the hidden label visible. A repaint that rebuilt the key's
    /// content from the label alone would drop the arrow while the dwell outline, the id and the
    /// name stayed healthy.
    /// </summary>
    [StaFact]
    public void ShippedQwertz_ArrowFrames_SurviveCapsAndShiftRepaints()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var modifier in new[] { ModifierKey.CapsLock, ModifierKey.Shift })
            {
                foreach (var action in new[] { "toggle:capslock", "modifier:shift", "enter", "tab" })
                {
                    var before = ArrowFrame(window, layout, action);
                    Check.WithCustomMessage($"{modifier}: the '{action}' key has no drawn arrow to begin with")
                         .That(before).IsNotNull();

                    vm.ToggleModifier(modifier);
                    Layout(window);
                    var on = ArrowFrame(window, layout, action);
                    Check.WithCustomMessage(
                            $"{modifier} on dropped the arrow (content {ContentKind(window, layout, action)}): "
                            + "the frame must survive every repaint.")
                         .That(on).IsNotNull();
                    Check.That(on!.Value.Canvas.Width).IsEqualTo(before!.Value.Canvas.Width);
                    Check.That(on.Value.Canvas.Height).IsEqualTo(before.Value.Canvas.Height);
                    Check.That(on.Value.Arrow.Data.Bounds).IsEqualTo(before.Value.Arrow.Data.Bounds);
                    Check.That(on.Value.Arrow.Stroke).IsSameReferenceAs(on.Value.Label.Foreground);
                    Check.That(on.Value.Label.Opacity).IsEqualTo(0.0);

                    vm.ToggleModifier(modifier);
                    Layout(window);
                    Check.WithCustomMessage($"{modifier} off dropped the arrow on '{action}'")
                         .That(ArrowFrame(window, layout, action)).IsNotNull();
                }
            }
        }
        finally { window.Close(); }
    }

    /// <summary>Each drawn-arrow key exposes exactly one named automation element — its hidden
    /// label — with the layout's German name and the unchanged AutomationId.</summary>
    [StaFact]
    public void ShippedQwertz_ArrowKeys_ExposeExactlyOneNamedUiaElement()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);
        try
        {
            foreach (var (action, name) in new[]
                     {
                         ("toggle:capslock", "Feststelltaste"),
                         ("modifier:shift", "Umschalttaste"),
                         ("enter", "Eingabetaste"),
                         ("tab", "Tabulatortaste"),
                     })
            {
                var visual = KeyVisuals(window, layout, "letters")
                    .Single(v => v.Key.SpecialAction == action);
                var named = NamedUiaElements(visual.Container);
                Check.WithCustomMessage(
                        $"{action}: UIA elements under the key: "
                        + string.Join("; ", named.Select(n => $"'{n.Name}' id='{n.Id}'")))
                     .That(named).HasSize(1);
                Check.That(named[0].Name).IsEqualTo(name);
                Check.That(named[0].Id).IsEqualTo($"Keyboard_Key_{action}");
            }
        }
        finally { window.Close(); }
    }

    private static (TextBlock Label, Canvas Canvas, Path Arrow)? ArrowFrame(
        KeyboardWindow window, KeyboardLayout layout, string action)
    {
        var visual = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == action);
        if (visual.Content is not Grid holder) return null;
        var canvas = holder.Children.OfType<Canvas>().SingleOrDefault();
        var paths = canvas is null ? [] : FramePaths(canvas);
        return paths.Count == 1 ? (visual.Label, canvas!, paths[0]) : null;
    }

    /// <summary>The named peers under an element, as UIA would build the tree.</summary>
    private static List<(string Name, string Id)> NamedUiaElements(DependencyObject node)
    {
        var named = new List<(string, string)>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement element
                && UIElementAutomationPeer.CreatePeerForElement(element) is { } peer
                && !string.IsNullOrEmpty(peer.GetName()))
                named.Add((peer.GetName(), peer.GetAutomationId()));
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(node);
        return named;
    }

    /// <summary>The drawn ink of one key at 192 dpi, the designer's method: the bounding box of the
    /// pixels at least halfway from the key's own fill to the caption colour (±0.5 DIP).</summary>
    private static Rect MeasureInk(Border container, FrameworkElement root)
    {
        var bounds = container.TransformToAncestor(root).TransformBounds(new Rect(container.RenderSize));
        int x = (int)Math.Round(bounds.X * 2), y = (int)Math.Round(bounds.Y * 2);
        int w = (int)Math.Round(bounds.Width * 2), h = (int)Math.Round(bounds.Height * 2);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * 2), (int)Math.Ceiling(root.ActualHeight * 2),
            192, 192, PixelFormats.Pbgra32);
        bmp.Render(root);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(new Int32Rect(x, y, w, h), px, w * 4, 0);

        var fill = ((SolidColorBrush)container.Background).Color;
        var text = ((SolidColorBrush)Tokens.BrushTextPrimary).Color;
        double dr = text.R - fill.R, dg = text.G - fill.G, db = text.B - fill.B;
        var dd = dr * dr + dg * dg + db * db;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var yy = 0; yy < h; yy++)
            for (var xx = 0; xx < w; xx++)
            {
                var i = (yy * w + xx) * 4;
                var a = px[i + 3];
                if (a == 0) continue;
                double B = px[i] * 255.0 / a, G = px[i + 1] * 255.0 / a, R = px[i + 2] * 255.0 / a;
                var proj = ((R - fill.R) * dr + (G - fill.G) * dg + (B - fill.B) * db) / dd;
                if (proj < 0.5) continue;
                minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx);
                minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy);
            }
        return maxX < 0
            ? Rect.Empty
            : new Rect(minX / 2.0, minY / 2.0, (maxX - minX + 1) / 2.0, (maxY - minY + 1) / 2.0);
    }

    private static (TextBlock Label, Canvas Canvas, Path Arrow, Path Text)? EraseFrame(
        KeyboardWindow window, KeyboardLayout layout)
    {
        var visual = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == "deleteWord");
        if (visual.Content is not Grid holder) return null;
        var canvas = holder.Children.OfType<Canvas>().SingleOrDefault();
        var paths = canvas is null ? [] : FramePaths(canvas);
        return paths.Count == 2 ? (visual.Label, canvas!, paths[0], paths[1]) : null;
    }

    /// <summary>The frame holder's Canvas: the drawn erase arrow or Caps/Shift/Enter arrow.</summary>
    private static Canvas FrameCanvas(FrameworkElement content) =>
        ((Grid)content).Children.OfType<Canvas>().Single();

    /// <summary>The Paths inside a frame Canvas: [arrow, text] for eraseLeft, [arrow] for the arrows.</summary>
    private static List<Path> FramePaths(Canvas canvas) => canvas.Children.OfType<Path>().ToList();

    /// <summary>
    /// F2 (review-03): the framed caption must not be a second UIA element. A screen reader on the
    /// word-backspace key must hear its one name, "Wort löschen" with Keyboard_Key_deleteWord, and
    /// not "Wort löschen, Aa". The key's peer tree is walked the way UIA would build it: create a
    /// peer for every visual, keep the named ones. The drawing is Paths (no peers) around the
    /// caption label, which stays in the tree as the label's Opacity-0 automation anchor.
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

    private static string ContentKind(KeyboardWindow window, KeyboardLayout layout, string action = "deleteWord")
    {
        var visual = KeyVisuals(window, layout, "letters").Single(v => v.Key.SpecialAction == action);
        return visual.Content.GetType().Name;
    }

    /// <summary>The glyph geometry the erase frame draws its fill from, at a given font size.</summary>
    private static Geometry TextGeometry(string text, double size) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Tokens.FontStandard, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, Brushes.Black, 2.0)
            .BuildGeometry(new Point(0, 0));

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
    /// itself, or the erase frame's holder grid whose first child is the label (round 3).
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
            // The dwell grid's first child is the caption content: the label itself, or the erase
            // frame's holder (whose first child is the label). Read it from the container, never
            // from the label's parent — the frame holder is a Grid, like the dwell grid.
            var content = container.Child is Grid dwell && dwell.Children.Count > 0
                          && dwell.Children[0] is FrameworkElement caption
                ? caption
                : label;
            result.Add((id, byId[id], container, label, content));
        }
        return result;
    }

    /// <summary>The caption TextBlocks a key draws: the label alone, or (round 3) the frame holder's
    /// hidden anchor label.</summary>
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
