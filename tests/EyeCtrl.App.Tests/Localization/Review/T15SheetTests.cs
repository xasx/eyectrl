namespace EyeCtrl.App.Tests.Localization.Review;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T15's review sheets for the user: headless renders at the user's 2.0 of the two things T15
/// changes that a person can SEE. Not a gate: it writes only when <c>EYECTRL_WRITE_T15_SHEETS=1</c>
/// (into <c>EYECTRL_T15_SHEET_DIR</c>), and never shows a window, because the user controls this PC
/// by eye-tracking alone.
/// </summary>
[Collection("StringTable Tests (App)")]
public class T15SheetTests
{
    private static readonly Brush SheetBackground = Frozen(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA6));
    private static readonly Brush GermanInk = Frozen(Color.FromRgb(0x8F, 0xE3, 0x9B));
    private static readonly Brush FlagInk = Frozen(Color.FromRgb(0xF5, 0xC5, 0x42));
    private static readonly FontFamily SheetFont = new("Segoe UI");

    private readonly ITestOutputHelper _output;

    public T15SheetTests(ITestOutputHelper output) => _output = output;

    internal static bool Requested => Environment.GetEnvironmentVariable("EYECTRL_WRITE_T15_SHEETS") == "1";

    internal static string SheetDirectory =>
        Environment.GetEnvironmentVariable("EYECTRL_T15_SHEET_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "eyectrl-t15-sheets");

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, Thickness? margin = null) =>
        new()
        {
            Text = text,
            FontFamily = SheetFont,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0),
        };

    [StaFact]
    public void EmojiBackSheet_WhenAsked()
    {
        Assert.SkipUnless(Requested, "set EYECTRL_WRITE_T15_SHEETS=1 to write the T15 sheets");
        WpfApplicationGate.EnsureApplicationWithTokens();
        var path = ReviewRender.Save(
            ReviewRender.Snap(EmojiBackSheet(), 1920, null, background: SheetBackground),
            Path.Combine(SheetDirectory, "emoji-back-sheet.png"));
        _output.WriteLine(path);
    }

    // ────── F4: the emoji skin-tone selector's back key ──────

    private static FrameworkElement EmojiBackSheet()
    {
        var left = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
        var right = new StackPanel();

        left.Children.Add(Text("User's size: key size 1.5×", 22, Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var block in Blocks(1.5)) left.Children.Add(block);

        right.Children.Add(Text("Key size 1.0×", 22, Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var block in Blocks(1.0)) right.Children.Add(block);
        right.Children.Add(Text("Key size 0.5× (the smallest)", 22, Ink, FontWeights.SemiBold, new Thickness(0, 16, 0, 8)));
        foreach (var block in Blocks(0.5)) right.Children.Add(block);

        var columns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        columns.Children.Add(left);
        columns.Children.Add(right);

        var root = new StackPanel { Margin = new Thickness(40), Background = SheetBackground };
        root.Children.Add(Text("The emoji skin-tone selector: the back key", 34, Ink, FontWeights.SemiBold));
        root.Children.Add(Text(
            "Dwell the skin-tone tab and the six swatches appear; the key under them goes back to the emoji grid. "
            + "Before: it said the English word \"Back\" on every language. After: it shows the arrow ◀ (the same arrow "
            + "as the emoji page-back key), and its spoken (screen-reader) name follows the language. "
            + "Same key, same size, same place. Rendered headless at 2.0.",
            18, Dim, margin: new Thickness(0, 6, 0, 18)));
        root.Children.Add(columns);
        return root;
    }

    private static IEnumerable<FrameworkElement> Blocks(double keyMultiplier)
    {
        var keySize = 70 * keyMultiplier;

        yield return Block(
            "BEFORE, German session (the shipped code)", keySize, German: true, before: true, spoken: null);
        yield return Block("AFTER, English session", keySize, German: false, before: false,
            spoken: "Back");
        yield return Block("AFTER, German session", keySize, German: true, before: false,
            spoken: "Zurück");
    }

    private static FrameworkElement Block(string title, double keySize, bool German, bool before, string? spoken)
    {
        StringTable.ResetToEmbeddedEnglish();
        try
        {
            if (German) EyeCtrl.App.Tests.Localization.ShippedLanguageRecoveryTests.ActivateShippedGerman();

            var rows = new StackPanel();
            var registrations = new List<(string Id, TextBlock? Content)>();
            var manager = new EmojiGridManager(
                keyRows: rows,
                categoryBar: new StackPanel(),
                registerKeyVisual: (id, key, container, content) =>
                {
                    registrations.Add((id, content));
                    DwellOutlineOverlay.Attach(container, content);
                },
                getKeySize: () => keySize,
                clearKeyState: () => { });
            manager.SetEmojiData(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json"),
                Path.Combine(Path.GetTempPath(), "eyectrl_t15_sheet_" + Guid.NewGuid().ToString("N") + ".json"));
            manager.Enter();
            registrations.Clear();
            manager.HandleAction("skinTone");

            var back = registrations.Single(r => r.Id == "special_skinToneBack").Content!;
            // The "before" picture is today's code: the same key with its caption the English word.
            if (before) back.Text = "Back";
            var captionLine = before
                ? "caption \"Back\" (English, under German); spoken name: \"Back\""
                : $"caption \"◀\"; spoken name: \"{AutomationProperties.GetName(back)}\"";
            if (spoken is not null)
                System.Diagnostics.Debug.Assert(AutomationProperties.GetName(back) == spoken);

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(Text(title, 17, German ? GermanInk : Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 2)));
            panel.Children.Add(Text(captionLine, 15, before ? FlagInk : Dim, margin: new Thickness(0, 0, 0, 4)));
            panel.Children.Add(rows);
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return panel;
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }
}
