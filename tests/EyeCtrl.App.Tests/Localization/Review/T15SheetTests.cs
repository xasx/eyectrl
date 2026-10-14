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
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T15's review sheets for the user: headless renders at the user's 2.0 of the two things T15
/// changes that a person can SEE. Not a gate: it writes only when <c>EYECTRL_WRITE_T15_SHEETS=1</c>
/// (into <c>EYECTRL_T15_SHEET_DIR</c>), and never shows a window, because the user controls this PC
/// by eye-tracking alone. Round 2 (the user's reply to the first sheets): the emoji back key as the
/// keyboard's drawn arrow, and the restart notice at a size a gaze user can read.
/// </summary>
[Collection("StringTable Tests (App)")]
public class T15SheetTests
{
    private static readonly Brush SheetBackground = Frozen(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA6));
    private static readonly Brush GermanInk = Frozen(Color.FromRgb(0x8F, 0xE3, 0x9B));
    private static readonly Brush FlagInk = Frozen(Color.FromRgb(0xF5, 0xC5, 0x42));
    private static readonly Brush Desktop = Frozen(Color.FromRgb(0x3B, 0x4A, 0x5A));
    private static readonly FontFamily SheetFont = new("Segoe UI");

    /// <summary>The user's screen at 2.0 less the taskbar, in DIP.</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

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

    private static void ActivateLanguage(bool german)
    {
        StringTable.ResetToEmbeddedEnglish();
        if (german) EyeCtrl.App.Tests.Localization.ShippedLanguageRecoveryTests.ActivateShippedGerman();
    }

    [StaFact]
    public void EmojiBackSheetV2_WhenAsked()
    {
        Assert.SkipUnless(Requested, "set EYECTRL_WRITE_T15_SHEETS=1 to write the T15 sheets");
        WpfApplicationGate.EnsureApplicationWithTokens();
        var path = ReviewRender.Save(
            ReviewRender.Snap(EmojiBackSheet(), 1960, null, background: SheetBackground),
            Path.Combine(SheetDirectory, "emoji-back-sheet-v2.png"));
        _output.WriteLine(path);
    }

    [StaFact]
    public void RestartNoticeSheetV3_WhenAsked()
    {
        Assert.SkipUnless(Requested, "set EYECTRL_WRITE_T15_SHEETS=1 to write the T15 sheets");
        WpfApplicationGate.EnsureApplicationWithTokens();
        var path = ReviewRender.Save(
            ReviewRender.Snap(RestartNoticeSheet(), 2160, null, background: SheetBackground),
            Path.Combine(SheetDirectory, "restart-notice-sheet-v3.png"));
        _output.WriteLine(path);
    }

    private static FrameworkElement Picture(System.Windows.Media.Imaging.BitmapSource bitmap) =>
        new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.None,
            Width = ReviewRender.DipSize(bitmap).Width,
            Height = ReviewRender.DipSize(bitmap).Height,
        };

    // ────── F4: the emoji skin-tone selector's back key, drawn like the keyboard's arrows ──────

    private static FrameworkElement EmojiBackSheet()
    {
        var left = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
        var right = new StackPanel();

        left.Children.Add(Text("User's size: key size 1.5×", 22, Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var block in Blocks(1.5)) left.Children.Add(block);
        left.Children.Add(Text("OPTION, not applied: the emoji grid's own page keys, drawn the same way (1.5×)", 20, FlagInk,
            FontWeights.SemiBold, new Thickness(0, 6, 0, 8)));
        left.Children.Add(PageKeys("NOW: font glyphs ◀ ▶ (as shipped)", 105, drawn: false));
        left.Children.Add(PageKeys("OPTION: the keyboard's drawn arrows", 105, drawn: true));

        right.Children.Add(Text("Key size 1.0×", 22, Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var block in Blocks(1.0)) right.Children.Add(block);
        right.Children.Add(Text("Key size 0.5× (the smallest)", 22, Ink, FontWeights.SemiBold, new Thickness(0, 16, 0, 8)));
        foreach (var block in Blocks(0.5)) right.Children.Add(block);

        var columns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        columns.Children.Add(left);
        columns.Children.Add(right);

        var root = new StackPanel();
        root.Children.Add(Text("The emoji skin-tone selector: the back key, round 2", 34, Ink, FontWeights.SemiBold));
        root.Children.Add(Text(
            "Round 1's ◀ was a font glyph, about 18 DIP in a 200 DIP key. Now the key draws the keyboard's own left arrow "
            + "(the wide drawn arrows of T14, the same ← the cursor-key row has): a third of the key's width, the keyboard's "
            + "stroke. Same key, same size, same place; its spoken name still follows the language. "
            + "Bottom left: the emoji grid's page ◀ ▶ keys, as an OPTION for consistency (not changed in the code). "
            + "Rendered headless at 2.0.",
            18, Dim, margin: new Thickness(0, 6, 0, 18)));
        root.Children.Add(columns);
        return Padded(root);
    }

    /// <summary>The sheet's 40 DIP margin as padding: an element's own Margin is outside what the render draws.</summary>
    private static FrameworkElement Padded(UIElement content) =>
        new Border { Padding = new Thickness(40), Background = SheetBackground, Child = content };

    private static IEnumerable<FrameworkElement> Blocks(double keyMultiplier)
    {
        var keySize = 70 * keyMultiplier;
        yield return Block("BEFORE, German session (the shipped code)", keySize, german: true, before: true);
        yield return Block("AFTER, English session", keySize, german: false, before: false);
        yield return Block("AFTER, German session", keySize, german: true, before: false);
    }

    private static EmojiGridManager NewManager(StackPanel rows, double keySize, List<(string Id, Border Container, TextBlock? Label)> registrations)
    {
        var manager = new EmojiGridManager(
            keyRows: rows,
            categoryBar: new StackPanel(),
            registerKeyVisual: (id, key, container, label, caption) =>
            {
                registrations.Add((id, container, label));
                DwellOutlineOverlay.Attach(container, caption ?? label);
            },
            getKeySize: () => keySize,
            clearKeyState: () => { });
        manager.SetEmojiData(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json"),
            Path.Combine(Path.GetTempPath(), "eyectrl_t15_sheet_" + Guid.NewGuid().ToString("N") + ".json"));
        manager.Enter();
        return manager;
    }

    private static FrameworkElement Block(string title, double keySize, bool german, bool before)
    {
        try
        {
            ActivateLanguage(german);
            var rows = new StackPanel();
            var registrations = new List<(string Id, Border Container, TextBlock? Label)>();
            var manager = NewManager(rows, keySize, registrations);
            registrations.Clear();
            manager.HandleAction("skinTone");

            var back = registrations.Single(r => r.Id == "special_skinToneBack");
            string captionLine;
            if (before)
            {
                // Today's shipped key: the English word, whatever the language.
                var key = new TextBlock
                {
                    Text = "Back", Foreground = Tokens.BrushTextPrimary,
                    FontSize = Math.Max(keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                back.Container.Child = null;
                DwellOutlineOverlay.Attach(back.Container, key);
                captionLine = "caption \"Back\" (English, under German); spoken name: \"Back\"";
            }
            else
            {
                captionLine = $"drawn arrow, {ArrowWidth(back.Container):0.#} DIP wide in a {back.Container.Width:0.#} DIP key; "
                              + $"spoken name: \"{AutomationProperties.GetName(back.Label!)}\"";
            }

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(Text(title, 17, german ? GermanInk : Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 2)));
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

    private static double ArrowWidth(Border container)
    {
        // container.Child is the outline layout Grid; the arrow canvas is inside the caption holder.
        static IEnumerable<DependencyObject> Walk(DependencyObject node)
        {
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                foreach (var inner in Walk(child))
                    yield return inner;
        }

        return Walk(container).OfType<Canvas>().First().Width;
    }

    /// <summary>The emoji grid's bottom row (page-back, ABC, skin tone, page-next), now or with drawn page arrows.</summary>
    private static FrameworkElement PageKeys(string title, double keySize, bool drawn)
    {
        StringTable.ResetToEmbeddedEnglish();
        var rows = new StackPanel();
        var registrations = new List<(string Id, Border Container, TextBlock? Label)>();
        var manager = NewManager(rows, keySize, registrations);
        registrations.Clear();
        manager.HandleAction("emojiPage:next");
        registrations.RemoveAll(r => !r.Id.StartsWith("special_", StringComparison.Ordinal));
        var prev = registrations.SingleOrDefault(r => r.Id == "special_emojiPage:prev");
        var next = registrations.SingleOrDefault(r => r.Id == "special_emojiPage:next");
        if (prev.Container is null || next.Container is null)
            throw new InvalidOperationException("the second emoji page has no prev/next pair; pick a category with 3+ pages");

        if (drawn)
        {
            foreach (var (frame, key) in new[] { (ArrowLabelFrame.CursorLeft, prev), (ArrowLabelFrame.CursorRight, next) })
            {
                var anchor = new TextBlock
                {
                    Text = frame == ArrowLabelFrame.CursorLeft ? "←" : "→",
                    Foreground = Tokens.BrushTextPrimary,
                    FontSize = Math.Max(keySize * ContainerRatio.Small, Legibility.MinimumGlyphFontSize) * SymbolCaptionRule.Scale,
                };
                var holder = ArrowLabelFrame.Build(frame, anchor, keySize, inkWidth: keySize / 3);
                key.Container.Child = null;
                DwellOutlineOverlay.Attach(key.Container, holder);
            }
        }

        var bottom = (Panel)rows.Children[^1];
        rows.Children.Remove(bottom);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(Text(title, 16, Dim, margin: new Thickness(0, 0, 0, 2)));
        panel.Children.Add(bottom);
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel;
    }

    // ────── F5: the restart notices when no Settings window is open, round 2 ──────

    private static FrameworkElement RestartNoticeSheet()
    {
        var root = new StackPanel();
        root.Children.Add(Text("Restart notices when Settings is closed, round 3", 34, Ink, FontWeights.SemiBold));
        root.Children.Add(Text(
            "Round 2's body (18 DIP) was still small. Now the BODY is FontSizeTitle (24 DIP) and the button label is the same "
            + "24 DIP. The first line stays distinguishable by size: it is FontSizeDisplay (34 DIP, the next token up, medium "
            + "weight). The card is 1000 DIP wide (round 2: 920; round 1: 720), the button 320 x 100 DIP (round 2: 260 x 84); "
            + "longer text wraps and the card grows upward, so German never clips. The card still never covers the gaze keyboard "
            + "(row 3). Rendered headless at 2.0.",
            18, Dim, margin: new Thickness(0, 6, 0, 20)));

        root.Children.Add(Row("1. The same notice, round 2 and round 3 (German, \"restarting\")",
            [("ROUND 2 (what you saw): 920 DIP wide, title 24, body 18, button 260 x 84", V2Notice(), false),
             ("ROUND 3: 1000 DIP wide, title 34, body 24, button 320 x 100", Notice(true, false), true)]));
        root.Children.Add(Row("2. Round 3, all four (English left, German right)",
            [("\"Restarting\": English", Notice(false, false), false), ("\"Restarting\": German", Notice(true, false), true)]));
        root.Children.Add(Row(null,
            [("\"Not possible\": English", Notice(false, true), false), ("\"Not possible\": German", Notice(true, true), true)]));
        root.Children.Add(Row("3. Where it sits on the user's 1920 x 1200 DIP screen (a sketch)",
            [("Gaze keyboard closed", Placement(withKeyboard: false), false),
             ("Gaze keyboard open at key size 1.5×: the card rises above it", Placement(withKeyboard: true), false)]));
        return Padded(root);
    }

    private static FrameworkElement Row(string? heading, (string Caption, FrameworkElement Content, bool German)[] cells)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        if (heading is not null)
            row.Children.Add(Text(heading, 22, Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (caption, content, german) in cells)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
            cell.Children.Add(Text(caption, 16, german ? GermanInk : Dim, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
            cell.Children.Add(content);
            line.Children.Add(cell);
        }

        row.Children.Add(line);
        return row;
    }

    private static string RefusalText(UiLanguage target) =>
        SelfRestartStrategy.Describe(RestartOutcome.ChildNotReady, target);

    private static FrameworkElement Notice(bool german, bool refused)
    {
        try
        {
            ActivateLanguage(german);
            var target = german ? UiLanguage.German : UiLanguage.English;
            var window = new RestartNoticeWindow();
            var (message, label) = refused
                ? (RefusalText(target), RestartNotices.RefusedChoices(target)[0].Label)
                : (RestartNotices.InProgressMessage(target), RestartNotices.InProgressChoices(target)[0].Label);
            window.SetText(message);
            window.CloseLabel.Text = label;
            window.Prepare(WorkArea);
            return Picture(Render(window));
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource Render(RestartNoticeWindow window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        return ReviewRender.Snap(content, window.Width, window.Height, window, background: Desktop);
    }

    /// <summary>Round 2's card, rebuilt from its recorded sizes, for the side-by-side.</summary>
    private static FrameworkElement V2Notice()
    {
        try
        {
            ActivateLanguage(true);
            var split = RestartNotices.InProgressMessage(UiLanguage.German).Split('\n', 2);
            var label = RestartNotices.InProgressChoices(UiLanguage.German)[0].Label;
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = split[0], Foreground = Tokens.BrushTextPrimary, FontSize = Tokens.FontSizeTitle,
                FontWeight = Tokens.FontWeightMedium, TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = split[1], Foreground = Tokens.BrushTextPrimary, FontSize = Tokens.FontSizeSubtitle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            });
            stack.Children.Add(new Border
            {
                Width = 260, Height = 84, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
                Background = Tokens.BrushSurfaceKeySwitch, CornerRadius = Tokens.StandardCornerRadius,
                Child = new TextBlock
                {
                    Text = label, Foreground = Tokens.BrushTextPrimary, FontSize = Tokens.FontSizeSubtitle,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var banner = new Border
            {
                Width = 920, Padding = new Thickness(36, 26, 36, 26), Background = Tokens.BrushSurfaceDark,
                BorderBrush = Tokens.BrushBorderSubtle, BorderThickness = new Thickness(1),
                CornerRadius = Tokens.StandardCornerRadius, Child = stack,
            };
            return Picture(ReviewRender.Snap(banner, background: Desktop, family: Tokens.FontStandard));
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static FrameworkElement Placement(bool withKeyboard)
    {
        Rect? keyboard = null;
        if (withKeyboard)
        {
            var probe = RestartNoticePlacementTests.MeasureKeyboard(1.5);
            var (kl, kt) = OverlayPositioning.PlaceAtEdge(WorkArea, "bottom", probe.Chrome.Width, probe.Chrome.Height);
            keyboard = new Rect(kl, kt, probe.Chrome.Width, probe.Chrome.Height);
        }

        var window = new RestartNoticeWindow(keyboardBounds: () => keyboard);
        try
        {
            ActivateLanguage(true);
            window.SetText(RefusalText(UiLanguage.German));
            window.CloseLabel.Text = RestartNotices.RefusedChoices(UiLanguage.German)[0].Label;
            window.Prepare(WorkArea);
            var (left, top) = (window.Left, window.Top);
            var card = Picture(Render(window));

            var screen = new Canvas { Width = 1920, Height = 1200, Background = Desktop };
            void Add(FrameworkElement element, double x, double y)
            {
                Canvas.SetLeft(element, x);
                Canvas.SetTop(element, y);
                screen.Children.Add(element);
            }

            Border Box(double w, double h, string label, Color fill) => new()
            {
                Width = w, Height = h, Background = Frozen(fill), CornerRadius = new CornerRadius(8),
                Child = Text(label, 26, Dim, margin: new Thickness(16, 12, 0, 0)),
            };

            Add(Box(700, 112, "toolbar (docked top by default)", Color.FromRgb(0x24, 0x24, 0x28)), (1920 - 700) / 2.0, 0);
            Add(Box(112, 560, "", Color.FromRgb(0x24, 0x24, 0x28)), 1920 - 112, (1152 - 560) / 2.0);
            Add(Box(1920, 48, "taskbar", Color.FromRgb(0x1A, 0x1A, 0x1E)), 0, 1152);
            if (keyboard is { } kb)
                Add(Box(kb.Width, kb.Height, "gaze keyboard (1.5×)", Color.FromRgb(0x2B, 0x2B, 0x32)), kb.X, kb.Y);
            Add(card, left, top);

            var thumb = new Viewbox { Width = 900, Height = 562.5, Stretch = Stretch.Uniform, Child = screen };
            thumb.Measure(new Size(900, 562.5));
            thumb.Arrange(new Rect(0, 0, 900, 562.5));
            return thumb;
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            window.Close();
        }
    }
}
