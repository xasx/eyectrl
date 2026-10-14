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
using EyeCtrl.App.Views;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Enums;
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

    [StaFact]
    public void RestartNoticeSheet_WhenAsked()
    {
        Assert.SkipUnless(Requested, "set EYECTRL_WRITE_T15_SHEETS=1 to write the T15 sheets");
        WpfApplicationGate.EnsureApplicationWithTokens();
        var path = ReviewRender.Save(
            ReviewRender.Snap(RestartNoticeSheet(), 1920, null, background: SheetBackground),
            Path.Combine(SheetDirectory, "restart-notice-sheet.png"));
        _output.WriteLine(path);
    }

    // ────── F5: the restart notices when no Settings window is open ──────

    private static readonly Brush Desktop = Frozen(Color.FromRgb(0x3B, 0x4A, 0x5A));

    private static FrameworkElement RestartNoticeSheet()
    {
        var root = new StackPanel { Margin = new Thickness(40), Background = SheetBackground };
        root.Children.Add(Text("Restart notices when Settings is closed", 34, Ink, FontWeights.SemiBold));
        root.Children.Add(Text(
            "When EyeCtrl restarts and no Settings window is open (for example a language change made from the "
            + "Supervisor), this card appears: bottom centre, always on top, never taking the foreground. "
            + "Its one button is dwelled like every other (it fills while you look at it) and closes the card; "
            + "if nobody looks at it, the card closes by itself: \"restarting\" after 40 s, \"not possible\" after 30 s. "
            + "The text is the text Settings already shows in the same situation. If Settings is open, it shows its "
            + "own notice and this card stays away, so nothing appears twice. Rendered headless at 2.0.",
            18, Dim, margin: new Thickness(0, 6, 0, 20)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        Cell(grid, 0, 0, "1. \"Restarting\": English session", Notice(german: false, refused: false, progress: 0), false);
        Cell(grid, 1, 0, "2. \"Restarting\": German session", Notice(german: true, refused: false, progress: 0), true);
        Cell(grid, 0, 1, "3. \"Not possible\": English session", Notice(german: false, refused: true, progress: 0), false);
        Cell(grid, 1, 1, "4. \"Not possible\": German session", Notice(german: true, refused: true, progress: 0), true);
        Cell(grid, 0, 2, "5. The button 60 % through its dwell (English)", Notice(german: false, refused: true, progress: 0.6), false);
        Cell(grid, 1, 2, "6. Where it sits on a 1920 x 1080 screen (a sketch)", Placement(), false);
        root.Children.Add(grid);
        return root;
    }

    private static void Cell(Grid grid, int column, int row, string title, FrameworkElement content, bool german)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 40, 24) };
        cell.Children.Add(Text(title, 17, german ? GermanInk : Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
        cell.Children.Add(content);
        Grid.SetColumn(cell, column);
        Grid.SetRow(cell, row);
        grid.Children.Add(cell);
    }

    private static FrameworkElement Notice(bool german, bool refused, double progress)
    {
        StringTable.ResetToEmbeddedEnglish();
        try
        {
            if (german) EyeCtrl.App.Tests.Localization.ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var target = german ? UiLanguage.German : UiLanguage.English;

            var window = new RestartNoticeWindow();
            var (message, label) = refused
                ? (SelfRestartStrategy.Describe(RestartOutcome.ChildNotReady, target),
                   RestartNotices.RefusedChoices(target)[0].Label)
                : (RestartNotices.InProgressMessage(target),
                   RestartNotices.InProgressChoices(target)[0].Label);
            window.Message.Text = message;
            window.CloseLabel.Text = label;
            window.Prepare(new Rect(0, 0, 1920, 1080));
            var outline = (DwellOutlineOverlay)typeof(RestartNoticeWindow)
                .GetField("_outline", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(window)!;
            outline.SetProgress(progress);

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var bitmap = ReviewRender.Snap(content, window.Width, window.Height, window, background: Desktop);
            return new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = Stretch.None,
                Width = ReviewRender.DipSize(bitmap).Width,
                Height = ReviewRender.DipSize(bitmap).Height,
            };
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static FrameworkElement Placement()
    {
        var card = (System.Windows.Controls.Image)Notice(german: false, refused: true, progress: 0);
        var window = new RestartNoticeWindow();
        window.Prepare(new Rect(0, 0, 1920, 1080));
        var (left, top) = (window.Left, window.Top);

        var screen = new Canvas { Width = 1920, Height = 1080, Background = Desktop };
        var toolbar = new Border
        {
            Width = 700, Height = 112, Background = Frozen(Color.FromRgb(0x24, 0x24, 0x28)),
            Child = Text("toolbar (docked top by default)", 24, Dim, margin: new Thickness(20, 40, 0, 0)),
        };
        Canvas.SetLeft(toolbar, (1920 - 700) / 2.0);
        Canvas.SetTop(toolbar, 0);
        var edge = new Border { Width = 112, Height = 560, Background = Frozen(Color.FromRgb(0x24, 0x24, 0x28)) };
        Canvas.SetLeft(edge, 1920 - 112);
        Canvas.SetTop(edge, (1080 - 560) / 2.0);
        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
        screen.Children.Add(toolbar);
        screen.Children.Add(edge);
        screen.Children.Add(card);

        var thumb = new Viewbox { Width = 960, Height = 540, Stretch = Stretch.Uniform, Child = screen };
        thumb.Measure(new Size(960, 540));
        thumb.Arrange(new Rect(0, 0, 960, 540));
        return thumb;
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
