namespace EyeCtrl.App.Tests.Localization.Review;

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The backdrop follow-up's review sheet: the three Settings → Advanced → Backdrop rows, rendered
/// headless at the user's 2.0 in English and German, for the one "Glass" option (Mica + Acrylic
/// merged). Not a gate: it writes only when <c>EYECTRL_WRITE_BACKDROP_SHEET=1</c> (into
/// <c>EYECTRL_BACKDROP_SHEET_DIR</c>, default a temp folder), and never shows a window — the owner
/// controls this PC by eye-tracking alone.
///
/// <para>
/// The type row is a gaze <c>DwellComboBox</c>: dwelling cycles its options, so there is no open
/// dropdown to render. Each column therefore carries the option list in order as a caption line —
/// the text the combo shows while cycling.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class BackdropGlassSheetTests
{
    private static readonly Brush SheetBackground = Frozen(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA6));
    private static readonly Brush GermanInk = Frozen(Color.FromRgb(0x8F, 0xE3, 0x9B));
    private static readonly FontFamily SheetFont = new("Segoe UI");

    /// <summary>The Settings content column at the owner's 1920 DIP display (the two-column pitch).</summary>
    private const double ColumnWidth = 1424;

    internal static bool Requested => Environment.GetEnvironmentVariable("EYECTRL_WRITE_BACKDROP_SHEET") == "1";

    internal static string SheetDirectory =>
        Environment.GetEnvironmentVariable("EYECTRL_BACKDROP_SHEET_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "eyectrl-backdrop-sheet");

    private readonly ITestOutputHelper _output;

    public BackdropGlassSheetTests(ITestOutputHelper output) => _output = output;

    [StaFact]
    public void BackdropGlassRowsSheet_WhenAsked()
    {
        Assert.SkipUnless(Requested, "set EYECTRL_WRITE_BACKDROP_SHEET=1 to write the backdrop sheet");
        WpfApplicationGate.EnsureApplicationWithTokens();
        var path = ReviewRender.Save(
            ReviewRender.Snap(Sheet(), 2968, null, family: Tokens.FontStandard, background: SheetBackground),
            Path.Combine(SheetDirectory, "backdrop-glass-rows.png"));
        _output.WriteLine(path);
    }

    private static FrameworkElement Sheet()
    {
        var root = new StackPanel();
        root.Children.Add(Text("Settings → Advanced → Backdrop: one \"Glass\" option, English and German", 34, Ink, FontWeights.SemiBold));
        root.Children.Add(Text(
            "Mica and Acrylic drew the same material, so they are one option now: Glass (German: Milchglas). "
            + "The rows below are the production controls (SettingsWindow.CreateControl over AdvancedPageSource), "
            + "rendered headless at 2.0. The type row is a gaze combo — dwelling cycles its options, so the list in "
            + "each footer is the option text in cycle order. An old settings file with \"mica\"/\"acrylic\" loads as "
            + "Glass with every other setting intact.",
            18, Dim, margin: new Thickness(0, 6, 0, 20)));

        var columns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        columns.Children.Add(Column("English session", german: false));
        columns.Children.Add(Column("German session", german: true));
        root.Children.Add(columns);
        return new Border { Padding = new Thickness(40), Background = SheetBackground, Child = root };
    }

    private static FrameworkElement Column(string caption, bool german)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
        column.Children.Add(Text(caption, 22, german ? GermanInk : Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));

        try
        {
            if (german) EyeCtrl.App.Tests.Localization.ShippedLanguageRecoveryTests.ActivateShippedGerman();

            var source = new AdvancedPageSource(EyeCtrl.App.Tests.Settings.Pages.TestSettingsPageContext.Build());
            var panel = new StackPanel { Width = ColumnWidth };
            foreach (var spec in source.Build().SelectMany(page => page.Rows)
                         .Where(row => row.Id.StartsWith("av_backdrop", StringComparison.Ordinal)))
            {
                var control = SettingsWindow.CreateControl(
                    spec, new SettingsAutoPagination.NoopDwellRegistrar(),
                    SettingsWindow.ControlBtnRadius, 800, ColumnWidth);
                ((IDwellComposite)control).Attach(panel);
            }

            column.Children.Add(panel);

            var strings = EyeCtrl.Core.Localization.StringTable.Current;
            column.Children.Add(Text(
                "option [type], in cycle order:  "
                + strings.T("settings.advanced.backdrop_type.option.glass") + "  →  "
                + strings.T("settings.advanced.backdrop_type.option.solid_darken"),
                16, Dim, margin: new Thickness(0, 10, 0, 0)));
        }
        finally
        {
            EyeCtrl.Core.Localization.StringTable.ResetToEmbeddedEnglish();
        }

        return column;
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

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
