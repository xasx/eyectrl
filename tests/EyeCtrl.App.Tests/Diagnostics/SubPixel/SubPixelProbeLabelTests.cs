namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// SP-4d fixup 01 (G1/G2, from review 01 F1): every probe button's label is CENTRED on both axes
/// and big enough for the SP-6 owner to read — especially the answer digits "1" and "2". Before
/// this, a stretched 14 DIP TextBlock drew its single line at the button's TOP edge, ~120 DIP from
/// where the owner aims, at the edge where the cue grows.
///
/// <para>
/// The pin builds the REAL headless window at the owner's 2.0 DPI, per registered theme and in
/// ENGLISH and GERMAN (the probe strings' German is complete in <c>strings.de.json</c>; the surface
/// is not yet in the shipped manifest, so this test activates it through an injected table, the
/// same way a pre-manifest German surface is measured). Per button it asserts: the font size EQUALS
/// the G2 value; the label's rendered content box (its character rects, not the stretched
/// TextBlock box) is centred within 4 DIP of the button centre; and that box sits inside the
/// button's inner rectangle — inside End test's 3 DIP rest border and clear of the full-progress
/// cue width. The English pass proves the fix; the German pass proves "Test beenden", "Später" and
/// "Starten" fit the same buttons.
/// </para>
///
/// <para>
/// Equality on the size, not the review's floor: G2 assigns exact sizes, and End test's label at
/// <c>FontSizeDisplay</c> (the fixup's f3) still fits its inner rectangle, so only the equality can
/// fail it. Activating themes and the string table writes process globals, so this class is in the
/// exclusive <c>ActiveTheme</c> collection and restores both.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class SubPixelProbeLabelTests
{
    /// <summary>G2's answer-digit size, as a literal: a mutant back to 14 DIP must fail here.</summary>
    private const double AnswerDigitsDip = 96;

    /// <summary>G1's centring tolerance, in DIP (layout is exact; the slack absorbs fractional metrics).</summary>
    private const double CentreToleranceDip = 4;

    /// <summary>The fit floor: the content box must sit at least this far inside the inner rectangle.</summary>
    private const double FitMarginDip = 0.5;

    /// <summary>Which G2 rule a button's label follows.</summary>
    private enum LabelKind
    {
        /// <summary>The bare answer digits: 96 DIP.</summary>
        AnswerDigits,

        /// <summary>A word label on a 420 × 170 screen button: the theme's <c>FontSizeDisplay</c>.</summary>
        ScreenButton,

        /// <summary>End test on its 280 × 120 button: the theme's <c>FontSizeTitle</c>.</summary>
        EndTest,
    }

    /// <summary>Every probe button, its G2 kind, and the phase that shows it.</summary>
    private static readonly (string Id, LabelKind Kind, SubPixelProbePhase Phase)[] Buttons =
    [
        (SubPixelProbeViewModel.AnswerOneTargetId, LabelKind.AnswerDigits, SubPixelProbePhase.Answer),
        (SubPixelProbeViewModel.AnswerTwoTargetId, LabelKind.AnswerDigits, SubPixelProbePhase.Answer),
        (SubPixelProbeViewModel.EndTestTargetId, LabelKind.EndTest, SubPixelProbePhase.Answer),
        (SubPixelProbeViewModel.IntroStartTargetId, LabelKind.ScreenButton, SubPixelProbePhase.Intro),
        (SubPixelProbeViewModel.AskYesTargetId, LabelKind.ScreenButton, SubPixelProbePhase.Ask),
        (SubPixelProbeViewModel.AskLaterTargetId, LabelKind.ScreenButton, SubPixelProbePhase.Ask),
        (SubPixelProbeViewModel.ResultDoneTargetId, LabelKind.ScreenButton, SubPixelProbePhase.Result),
    ];

    [StaFact]
    public void EveryRegisteredTheme_CentresAndFitsEveryProbeLabel_InEnglishAndGerman()
    {
        var germanTable = GermanProbeTable();
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                AssertLabels(theme, german: false);
                AssertLabels(theme, german: true, germanTable);
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    // Mutation (f1): VerticalAlignment.Center dropped -> the content box draws at the top edge, far
    //   from the button centre: the centring pin goes red in every theme and language.
    // Mutation (f2): the answer digits back to 14 DIP -> the equality pin goes red.
    // Mutation (f3): End test's label at Tokens.FontSizeDisplay -> the equality pin goes red.
    //   Measured: at 34 DIP the fit assertions alone stay green (the row still sits inside the
    //   inner rectangle), so the equality — not the fit — is f3's killer.

    private static void AssertLabels(ITheme theme, bool german, StringTable? germanTable = null)
    {
        if (german) StringTable.Activate(germanTable!);
        else StringTable.ResetToEmbeddedEnglish();

        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm);
        try
        {
            if (german)
            {
                // A toothless DE pass would silently re-measure English: the window resolves its
                // labels at construction, so the visible "Test beenden" proves the table rendered.
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, SubPixelProbePhase.Answer);
                Check.WithCustomMessage(
                        "The German table did not render for the probe: the shipped German for "
                        + "overlays.subpixel.end_test is 'Test beenden'.")
                    .That(LabelFor((Border)window.TargetElementForTest(
                        SubPixelProbeViewModel.EndTestTargetId)!).Text)
                    .IsEqualTo("Test beenden");
            }

            string language = german ? "DE" : "EN";
            foreach (var (id, kind, phase) in Buttons)
            {
                SetPhase(vm, SubPixelProbePhase.Idle);
                SetPhase(vm, phase);

                var button = (Border)window.TargetElementForTest(id)!;
                var label = LabelFor(button);
                double expectedFontSize = kind switch
                {
                    LabelKind.AnswerDigits => AnswerDigitsDip,
                    LabelKind.ScreenButton => theme.FontSizeDisplay,
                    _ => theme.FontSizeTitle,
                };

                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label is {label.FontSize} DIP, not the "
                        + $"G2 value {expectedFontSize}.")
                    .That(label.FontSize).IsEqualTo(expectedFontSize);

                var box = ContentBoxInButton(label, button);
                var buttonCentre = new Point(button.ActualWidth / 2, button.ActualHeight / 2);
                var boxCentre = new Point(
                    box.Left + (box.Width / 2), box.Top + (box.Height / 2));
                double offset = Math.Sqrt(
                    ((boxCentre.X - buttonCentre.X) * (boxCentre.X - buttonCentre.X))
                    + ((boxCentre.Y - buttonCentre.Y) * (boxCentre.Y - buttonCentre.Y)));
                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label box {box} is centred on {boxCentre}, "
                        + $"{offset:F1} DIP from the button centre {buttonCentre}; G1 caps it at "
                        + $"{CentreToleranceDip} DIP. ('{label.Text}')")
                    .That(offset).IsLessOrEqualThan(CentreToleranceDip);

                var inner = InnerRect(button);
                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label box {box} leaves only "
                        + $"{box.Left - inner.Left:F1} DIP inside the inner rectangle's left edge "
                        + $"({inner}); G2/SP-4d require the whole box inside it. ('{label.Text}')")
                    .That(box.Left - inner.Left).IsGreaterOrEqualThan(FitMarginDip);
                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label box {box} exceeds the inner "
                        + $"rectangle's right edge ({inner}) by {box.Right - inner.Right:F1} DIP.")
                    .That(inner.Right - box.Right).IsGreaterOrEqualThan(FitMarginDip);
                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label box {box} exceeds the inner "
                        + $"rectangle's top edge ({inner}) by {box.Top - inner.Top:F1} DIP.")
                    .That(box.Top - inner.Top).IsGreaterOrEqualThan(FitMarginDip);
                Check.WithCustomMessage(
                        $"{theme.Name}/{language}/{id}: the label box {box} exceeds the inner "
                        + $"rectangle's bottom edge ({inner}) by {box.Bottom - inner.Bottom:F1} DIP.")
                    .That(inner.Bottom - box.Bottom).IsGreaterOrEqualThan(FitMarginDip);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>
    /// The German table with the probe's surface activated: the shipped German file is complete for
    /// every <c>overlays</c> key, so the injected manifest list renders German for the probe while
    /// the shipped manifest (still empty at this base) keeps production on English.
    /// </summary>
    private static StringTable GermanProbeTable()
    {
        var path = Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Localization", StringTable.GermanFileName);
        return StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            LocalizationTestData.EnglishContent(),
            StringTableContent.ParseGerman(File.ReadAllText(path)),
            germanSurfaces: ["overlays"]);
    }

    /// <summary>
    /// The button's inner rectangle: inside its rest border (End test's 3 DIP; 0 elsewhere) and
    /// clear of the full-progress cue, which grows inward from the layout edge the border insets.
    /// </summary>
    private static Rect InnerRect(Border button)
    {
        double border = Math.Max(
            Math.Max(button.BorderThickness.Left, button.BorderThickness.Top),
            Math.Max(button.BorderThickness.Right, button.BorderThickness.Bottom));
        double inset = border + DwellOutlineOverlay.MaxThickness;
        return new Rect(
            inset, inset,
            button.ActualWidth - (2 * inset), button.ActualHeight - (2 * inset));
    }

    /// <summary>
    /// The label's RENDERED content box in button coordinates, from its own character rects. Not
    /// the TextBlock's layout box: an uncentred TextBlock stretches to the whole button and its box
    /// centre would coincide with the button centre while its line still draws at the top (the f1
    /// case), so only the character rects see where the text really is.
    /// </summary>
    private static Rect ContentBoxInButton(TextBlock label, FrameworkElement button)
    {
        var start = label.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var end = label.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var origin = label.TransformToAncestor(button).Transform(new Point(0, 0));

        double left = Math.Min(start.Left, end.Left);
        double right = Math.Max(start.Right, end.Right);
        double top = Math.Min(start.Top, end.Top);
        double bottom = Math.Max(start.Bottom, end.Bottom);
        return new Rect(
            origin.X + left, origin.Y + top, right - left, bottom - top);
    }

    /// <summary>The button's one TextBlock: the label BuildButton wraps in the cue overlay.</summary>
    private static TextBlock LabelFor(DependencyObject button)
    {
        var found = new List<TextBlock>();
        Collect(button, found);
        return found.First();
    }

    private static void Collect(DependencyObject node, List<TextBlock> into)
    {
        if (node is TextBlock tb) into.Add(tb);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Collect(VisualTreeHelper.GetChild(node, i), into);
    }

    private static SubPixelProbeViewModel NewViewModel() => new(
        new WeakReferenceMessenger(),
        new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        new EyeCtrlSettings().Dwell,
        () => true);

    /// <summary>The owner's configuration (2.0, 1920×1200 DIP), where the labels must fit.</summary>
    private static SubPixelProbeWindow NewWindow(SubPixelProbeViewModel vm)
    {
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => { }, scale: 2.0);
        window.Width = 1920;
        window.Height = 1200;
        new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    /// <summary>Drives the view to a phase headlessly through C5's observable Phase (SP-3 F8).</summary>
    private static void SetPhase(SubPixelProbeViewModel vm, SubPixelProbePhase phase) =>
        typeof(SubPixelProbeViewModel)
            .GetProperty(nameof(SubPixelProbeViewModel.Phase), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(vm, phase);
}
