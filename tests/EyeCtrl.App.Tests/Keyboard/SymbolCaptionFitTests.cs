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
/// laid out, never shown — no HWND, no <c>Show()</c>, no input), against the SHIPPED qwertz
/// layout:
///
/// <list type="number">
/// <item><b>No clipping</b> — every caption's advance fits its key box at all 16 allowed
/// <c>KeySizeMultiplier</c> steps (0.5..2.0 by 0.1), with the symbol rule applied.</item>
/// <item><b>The rule</b> — a symbol caption is drawn at the x1.4 fit-limited size in the theme's
/// medium weight; every other caption keeps today's clamped font and Normal weight.</item>
/// <item><b>Faces</b> — every caption renders with no tofu and every glyph run resolves to the
/// theme face, Segoe UI Symbol or Segoe UI Emoji.</item>
/// <item><b>Accessible names</b> — every key's UIA name is its layout <c>accessibleName</c> or,
/// absent one, its label; the AutomationId stays <c>Keyboard_Key_{id}</c>.</item>
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

    private static readonly string[] AllowedFamilies =
        ["Segoe UI Variable", "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji"];

    /// <summary>The final set's symbol captions (PHASE2-README.md section 1) — the labels the T14 rule must cover.</summary>
    private static readonly string[] ExpectedSymbolCaptions =
    [
        "\u21EA", "\u21E7", "\u232B", "\u232B\u232B", "\u2334", "\u21B5", "\u21E5",
        "\u2702", "\u21B6", "\u21B7", "\u2190", "\u2191", "\u2192", "\u2193", "\uD83D\uDE00",
    ];

    private readonly ITestOutputHelper _output;

    public SymbolCaptionFitTests(ITestOutputHelper output) => _output = output;

    private static KeyboardLayout ShippedQwertz() => KeyboardLayoutLoader.Load(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

    // ---- gate 1: no caption clips, at any allowed key size ----

    [StaFact]
    public void ShippedQwertz_NoCaptionClips_AtEveryAllowedKeySize()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, Mults[0]);

        var clipped = new List<string>();
        var notOneLine = new List<string>();
        var cases = new List<(double Side, string Where)>();
        var minAdvance = double.PositiveInfinity;
        try
        {
            foreach (var mult in Mults)
            {
                window.LoadLayout(layout, mult, KeyDwellMs, PredictionDwellMs, CooldownMs);
                foreach (var sub in layout.SubLayouts.Keys)
                {
                    vm.SwitchSubLayout(sub);
                    Layout(window);

                    foreach (var (id, _, container, label) in KeyVisuals(window, layout, sub))
                    {
                        Check.WithCustomMessage($"{sub}:{id} ('{label.Text}') was not arranged at {mult:0.0}x")
                             .That(container.ActualWidth).IsStrictlyGreaterThan(0.0);

                        // Clipping is the ADVANCE (what the text needs) exceeding the box. Not
                        // DesiredSize: when a label overflows, layout clamps DesiredSize to the
                        // available width, so the overflow is invisible there.
                        var advance = Advance(label);
                        minAdvance = Math.Min(minAdvance, advance);
                        var side = container.ActualWidth - advance;
                        cases.Add((side, $"{sub} '{label.Text}' ({id}) at {mult:0.0}x"));
                        if (advance > container.ActualWidth + 1e-6)
                            clipped.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: advance "
                                        + $"{advance:0.000} > box {container.ActualWidth:0.000}");

                        // The ticket draws the line at "one line, no wrapping or trimming". Pinned
                        // because the advance gate would still pass if a future edit enabled
                        // wrapping: the unwrapped advance is what it measures.
                        if (label.TextWrapping != TextWrapping.NoWrap || label.TextTrimming != TextTrimming.None)
                            notOneLine.Add($"{sub} '{label.Text}' ({id}) at {mult:0.0}x: "
                                          + $"TextWrapping={label.TextWrapping}, TextTrimming={label.TextTrimming}");
                    }
                }
            }

            Check.WithCustomMessage("no label advance was measured — the sweep measured nothing")
                 .That(minAdvance).IsStrictlyGreaterThan(0.0);
            Check.WithCustomMessage(
                    "Captions overflow their key box (T14's binding gate): "
                    + string.Join("; ", clipped))
                 .That(clipped).IsEmpty();
            Check.WithCustomMessage(
                    "Captions may wrap or trim (T14 requires one line, unwrapped, untrimmed): "
                    + string.Join("; ", notOneLine))
                 .That(notOneLine).IsEmpty();

            _output.WriteLine($"{cases.Count} key x size cases; no clipping. Tightest advances: "
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

                    foreach (var (id, key, container, label) in KeyVisuals(window, layout, sub))
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

    [StaFact]
    public void ShippedQwertz_EveryCaption_RendersWithAnAllowedFace_AndNoTofu()
    {
        var layout = ShippedQwertz();
        var (window, vm) = Open(layout, 1.5);

        var problems = new List<string>();
        var runCount = 0;
        try
        {
            foreach (var sub in layout.SubLayouts.Keys)
            {
                vm.SwitchSubLayout(sub);
                Layout(window);

                foreach (var (id, _, _, label) in KeyVisuals(window, layout, sub))
                {
                    var runs = Runs(label);
                    if (runs.Count == 0)
                    {
                        problems.Add($"{sub} '{label.Text}' ({id}): no glyph runs");
                        continue;
                    }
                    runCount += runs.Count;
                    if (runs.Any(r => r.Glyphs.Contains((ushort)0)))
                        problems.Add($"{sub} '{label.Text}' ({id}): TOFU (glyph index 0)");
                    var bad = runs.Select(r => r.Family).Distinct()
                        .Where(f => !AllowedFamilies.Contains(f)).ToList();
                    if (bad.Count > 0)
                        problems.Add($"{sub} '{label.Text}' ({id}): face(s) {string.Join(", ", bad)}");
                }
            }

            Check.WithCustomMessage("no glyph runs were read — the sweep measured nothing")
                 .That(runCount).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("Caption faces: " + string.Join("; ", problems))
                 .That(problems).IsEmpty();

            _output.WriteLine($"{runCount} glyph runs read; every one resolves to "
                + string.Join(", ", AllowedFamilies) + ", none is tofu.");
        }
        finally { window.Close(); }
    }

    // ---- gate 4: accessible names wired onto the real labels ----

    [StaFact]
    public void ShippedQwertz_AccessibleNames_AreWiredToTheKeyLabels()
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

                foreach (var (id, key, _, label) in KeyVisuals(window, layout, sub))
                {
                    var expected = key.AccessibleName ?? key.Label;
                    var actual = AutomationProperties.GetName(label);
                    if (actual != expected)
                        problems.Add($"{sub} '{label.Text}' ({id}): UIA name '{actual}', expected '{expected}'");
                    var automationId = AutomationProperties.GetAutomationId(label);
                    if (automationId != $"Keyboard_Key_{id}")
                        problems.Add($"{sub} '{label.Text}' ({id}): AutomationId '{automationId}'");
                    if (key.AccessibleName is not null)
                    {
                        named++;
                        if (actual == label.Text)
                            problems.Add($"{sub} '{label.Text}' ({id}): a symbol caption still announces its glyph");
                    }
                }
            }

            Check.WithCustomMessage("no accessible names were wired — the sweep measured nothing")
                 .That(named).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("UIA names: " + string.Join("; ", problems))
                 .That(problems).IsEmpty();

            _output.WriteLine($"{named} named-key cases; every key's UIA name is its "
                + "accessibleName or its label, and every AutomationId is Keyboard_Key_{id}.");
        }
        finally { window.Close(); }
    }

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
    /// containers and labels.
    /// </summary>
    private static List<(string Id, KeyDefinition Key, Border Container, TextBlock Label)> KeyVisuals(
        KeyboardWindow window, KeyboardLayout layout, string sub)
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

        var result = new List<(string, KeyDefinition, Border, TextBlock)>();
        foreach (DictionaryEntry entry in visuals)
        {
            var value = entry.Value!;
            var container = (Border)value.GetType().GetField("Item1")!.GetValue(value)!;
            var label = (TextBlock)value.GetType().GetField("Item2")!.GetValue(value)!;
            var id = (string)entry.Key;
            result.Add((id, byId[id], container, label));
        }
        return result;
    }

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
