namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// T09 PR 3 fixup-01 (review-04 D1a): the Settings modal is a RECORDED geometry exception. Its
/// message wraps, centred above the buttons, in a card centred in the window; a longer message
/// gives more lines, the card grows, and the buttons drop by HALF the extra height. That includes
/// T04's language-restart prompt, a crisis surface. It is accepted because the modal is laid out
/// once per show — <c>UpdateModalVisibility</c> sets the message and builds the buttons together,
/// before any dwell can start — and never re-lays out under a live dwell.
///
/// <para>
/// This pins that it is the ONLY movement: for every modal message, in English and under the
/// pseudo-locale, the stress table and T10's German draft, each button keeps its x and its size
/// exactly, and all buttons drop by one shared amount equal to half the card's growth. A button that moved sideways
/// or apart from the others, or a drop that does not come from the card, fails.
/// </para>
///
/// <para>
/// Built from production: the <c>ModalOverlay</c> element parsed out of <c>SettingsWindow.xaml</c>
/// (the window needs eight services and cannot be built headless), filled the way
/// <c>UpdateModalVisibility</c> fills it, through <see cref="SettingsWindow.BuildModalButton"/>, and
/// hosted at the window's size on the user's screen (0.9 × 1920 × 1200 DIP) at 2.0.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class SettingsModalExceptionTests
{
    private const double TolerancePx = 0.01;

    private readonly ITestOutputHelper _output;

    public SettingsModalExceptionTests(ITestOutputHelper output) => _output = output;

    /// <summary>The message keys the modal shows, with the choices each is shown with.</summary>
    public static TheoryData<string, string> Messages()
    {
        var data = new TheoryData<string, string>();
        foreach (var key in FitCatalog.FitByKey().Keys.Where(IsModalMessage).Order(StringComparer.Ordinal))
            data.Add(key, ChoicesFor(key));
        return data;
    }

    private static bool IsModalMessage(string key) =>
        key.EndsWith(".message", StringComparison.Ordinal)
        && (key.StartsWith("settings.", StringComparison.Ordinal) || key.StartsWith("language-recovery.", StringComparison.Ordinal));

    /// <summary>The choice set a message is shown with in production.</summary>
    private static string ChoicesFor(string key) => key switch
    {
        "language-recovery.prompt.message" => "language-recovery.prompt.restart|language-recovery.prompt.cancel",
        "language-recovery.restart.in_progress.message" => "language-recovery.restart.in_progress.hide",
        "settings.safety.intro.message" => "settings.safety.start|settings.safety.later",
        "settings.safety.unconfirmed.message" => "settings.safety.ok",
        "settings.safety.mode.message" => "settings.safety.mode.basic|settings.safety.mode.all",
        _ => "settings.modal.yes|settings.modal.no",
    };

    [StaTheory]
    [MemberData(nameof(Messages))]
    public void TheButtons_OnlyDropWithTheCard_UnderALongerTable(string messageKey, string choiceKeys)
    {
        var english = Layout(StringTable.EmbeddedEnglish, messageKey, choiceKeys);
        foreach (var table in new[] { ("pseudo", PseudoLocale.Table), ("stress", PseudoLocale.StressTable), ("german", GermanDraft.Table) })
        {
            var other = Layout(table.Item2, messageKey, choiceKeys);
            _output.WriteLine($"{messageKey} ({table.Item1}): card {Fmt(english.Card)} → {Fmt(other.Card)}");
            Check.WithCustomMessage($"{messageKey}: the {table.Item1} message is not translated, so nothing is compared.")
                .That(table.Item1 == "german" ? other.Message != english.Message : other.Message.StartsWith('[')).IsTrue();

            var problems = Problems(english, other);
            Check.WithCustomMessage($"{messageKey} ({table.Item1}): the modal moved its buttons beyond the recorded exception "
                                    + "(straight down, together, by half the card's growth):\n  " + string.Join("\n  ", problems))
                .That(problems).IsEmpty();
        }
    }

    /// <summary>
    /// The teeth of the exception's vertical rule. At the user's window width (1728 DIP) no modal
    /// message wraps today, even doubled — the card widens instead, keeping every button's x — so
    /// the drop the exception allows is exercised here with a message long enough to wrap: the
    /// card must grow, and the buttons must drop by exactly half of it, together.
    /// </summary>
    [StaFact]
    public void AWrappingMessage_DropsTheButtonsByHalfTheCardsGrowth_AndNothingElse()
    {
        var english = StringTable.EmbeddedEnglish;
        var labels = new[] { english.T("settings.modal.yes"), english.T("settings.modal.no") };
        var shortMessage = Layout(english.T("settings.general.reset.message"), labels);
        var longMessage = Layout(string.Join(" ", Enumerable.Repeat(english.T("settings.general.resetall.message"), 12)), labels);
        _output.WriteLine($"card {Fmt(shortMessage.Card)} → {Fmt(longMessage.Card)}");

        Check.WithCustomMessage("The long message did not wrap, so the drop rule was not exercised.")
            .That(longMessage.Card.Height).IsStrictlyGreaterThan(shortMessage.Card.Height + 20);
        var problems = Problems(shortMessage, longMessage);
        Check.WithCustomMessage("A wrapping message moved the buttons beyond half the card's growth:\n  " + string.Join("\n  ", problems))
            .That(problems).IsEmpty();
    }

    /// <summary>Everything that breaks the exception's one allowed movement.</summary>
    internal static List<string> Problems(ModalLayout english, ModalLayout other)
    {
        var problems = new List<string>();
        if (english.Buttons.Count != other.Buttons.Count)
        {
            problems.Add($"{english.Buttons.Count} buttons in English, {other.Buttons.Count} translated");
            return problems;
        }

        var allowedDrop = (other.Card.Height - english.Card.Height) / 2;
        for (var i = 0; i < english.Buttons.Count; i++)
        {
            var (e, o) = (english.Buttons[i], other.Buttons[i]);
            if (Math.Abs(e.X - o.X) > TolerancePx)
                problems.Add($"button {i}: moved sideways, x {FitText.Dip(e.X)} → {FitText.Dip(o.X)}");
            if (Math.Abs(e.Width - o.Width) > TolerancePx || Math.Abs(e.Height - o.Height) > TolerancePx)
                problems.Add($"button {i}: resized, {Fmt(e)} → {Fmt(o)}");
            if (Math.Abs(o.Y - e.Y - allowedDrop) > TolerancePx)
                problems.Add($"button {i}: dropped {FitText.Dip(o.Y - e.Y)}, but the card grew {FitText.Dip(other.Card.Height - english.Card.Height)} "
                             + $"(allowed: half, {FitText.Dip(allowedDrop)})");
        }

        return problems;
    }

    /// <summary>One modal laid out: the card's box, each button's box (window DIP), and the message.</summary>
    /// <param name="MessageBox">The message TextBlock's box (window DIP).</param>
    internal sealed record ModalLayout(Rect Card, IReadOnlyList<Rect> Buttons, string Message, Rect MessageBox);

    private static string Fmt(Rect r) => string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##} {2:0.##}×{3:0.##}", r.X, r.Y, r.Width, r.Height);

    /// <summary>The window's size on the user's screen: <c>WindowSizeRatio</c> of 1920 × 1200 DIP.</summary>
    private static Size WindowSize => new(1920 * SettingsWindow.WindowSizeRatio, 1200 * SettingsWindow.WindowSizeRatio);

    internal static ModalLayout Layout(StringTable strings, string messageKey, string choiceKeys) =>
        Layout(strings.T(messageKey), choiceKeys.Split('|').Select(k => strings.T(k)).ToList());

    internal static ModalLayout Layout(string messageText, IReadOnlyList<string> choices)
    {
        var (overlay, message, panel, card) = BuildModal(messageText, choices);
        using var host = FitHost.Open(overlay, width: WindowSize.Width, height: WindowSize.Height);
        Rect Box(FrameworkElement e) => e.TransformToAncestor(overlay).TransformBounds(new Rect(e.RenderSize));
        return new ModalLayout(Box(card), panel.Children.OfType<Border>().Select(Box).ToList(), message.Text, Box(message));
    }

    /// <summary>
    /// T10's review sheets (not a gate): the modal's card as the user sees it — the same build as
    /// <see cref="Layout(string, IReadOnlyList{string})"/>, hosted at the window's size at 2.0 and
    /// rendered (<see cref="ReviewRender"/>).
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource RenderCard(string messageText, IReadOnlyList<string> choices)
    {
        var (overlay, _, _, card) = BuildModal(messageText, choices);
        using var host = FitHost.Open(overlay, width: WindowSize.Width, height: WindowSize.Height);
        return ReviewRender.Render(card, card.RenderSize);
    }

    /// <summary>The modal overlay filled the way <c>UpdateModalVisibility</c> fills it, not yet laid out.</summary>
    private static (Grid Overlay, TextBlock Message, StackPanel Panel, Border Card) BuildModal(
        string messageText, IReadOnlyList<string> choices)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var overlay = ParseModalOverlay();
        var message = (TextBlock)overlay.FindName("ModalMessage");
        var panel = (StackPanel)overlay.FindName("ModalButtonPanel");
        var card = (Border)overlay.FindName("ModalContent");

        // As UpdateModalVisibility: the message, then the buttons — vertical for three or more.
        message.Text = messageText;
        var vertical = choices.Count > 2;
        panel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        var buttons = new Dictionary<string, Border>(StringComparer.Ordinal);
        var brushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        for (var i = 0; i < choices.Count; i++)
        {
            var (background, foreground) = ModalButtonPalette.For(i == 0 ? ModalChoiceKind.Affirmative : ModalChoiceKind.Negative);
            panel.Children.Add(SettingsWindow.BuildModalButton($"choice_{i}", choices[i], background, foreground,
                vertical, () => { }, buttons, brushes));
        }

        overlay.Visibility = Visibility.Visible;
        return (overlay, message, panel, card);
    }

    /// <summary><c>SettingsWindow.xaml</c>'s <c>ModalOverlay</c> grid, parsed on its own.</summary>
    private static Grid ParseModalOverlay()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var element = SettingsContentFitTests.Xml().Descendants()
            .SingleOrDefault(e => (string?)e.Attribute(x + "Name") == "ModalOverlay")
            ?? throw new InvalidOperationException("SettingsWindow.xaml has no x:Name=\"ModalOverlay\"; repoint this test.");
        var copy = new XElement(element);
        copy.Attributes().Where(a => a.Name.LocalName is "Grid.ColumnSpan" or "Grid.Row" or "Grid.Column").Remove();
        return (Grid)XamlReader.Parse(copy.ToString());
    }
}
