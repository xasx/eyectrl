namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The settings content card has to be VISIBLE, and it has to cost nothing.
///
/// <para>
/// It was neither. <c>ContentCard</c> shipped with <c>Background="{StaticResource
/// BrushSurfaceMedium}"</c> on a page whose background is <c>BrushSurfaceDark</c> — rendered and
/// inspected, a 1.23:1 fill step, which is no boundary at all. The card was invisible; all it did
/// was narrow the content column. Its stated purpose ("gives the row list an edge, so it reads as
/// a group rather than as items floating on the window") was not achieved.
/// </para>
/// <para>
/// The fix is the one this codebase already made once, for <c>DwellToggle</c>'s row card: palette
/// rule 1 puts the boundary on the BORDER — "the border carries the boundary; the fill only
/// suggests depth", <c>docs/design/overlay-unification-spec.md:284-286</c>, gated by
/// <c>tools/palette_validate.py:352-365</c> — drawn as a zero-layout sibling
/// (<c>ContentCardOutline</c>) rather than as the card's own <c>BorderThickness</c>.
/// </para>
/// <para>
/// WHY a sibling is the load-bearing half, and what <see cref="TheOutlineConsumesNoLayout"/>
/// exists for. A <see cref="Border"/> deflates its child by its <c>BorderThickness</c>. An outline
/// written on <c>ContentCard</c> would silently take <c>BorderThicknessStandard</c> per side off
/// the content column — 2 DIP in Midnight and Ember, 4 in Contrast — while
/// <c>SettingsWindow.ContentAvailableWidth</c>, which reads only the <c>Padding</c>, kept
/// reporting the full 416 on the narrowest supported display. That is exactly the nested-inset
/// drift <see cref="SettingsContentFitTests"/> exists to prevent, and it would over-cap the 460
/// DIP quick-action picker button by the same amount.
/// </para>
/// <para>
/// The perceptibility half runs per THEME rather than against whatever <c>Tokens.Current</c>
/// happens to be — same reason as <see cref="SettingsRowDescriptionContrastTests"/>: the theme
/// picker varies both tokens underneath this pairing, so a gate reading only the active theme
/// would let a new or edited theme ship an invisible card. It lives in <c>EyeCtrl.App.Tests</c>,
/// not <c>EyeCtrl.Controls.Tests</c>, because <c>Tokens.Activate</c> is banned there and nothing
/// here activates anything.
/// </para>
/// </summary>
public class SettingsContentCardBoundaryTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// A non-text user-interface component boundary owes 3:1 under WCAG 1.4.11, not the 4.5 body
    /// text owes. The card's edge carries no information beyond "the list stops here", so 3:1 is
    /// the right floor — but it is a floor, and 1.23 is not near it.
    /// </summary>
    private const double ComponentBoundaryFloor = 3.0;

    /// <summary>
    /// The narrow-but-real primary display the fit suite measures against: 1600x1200 at 150%, or
    /// 1920x1080 at 240%. The same number <see cref="SettingsContentFitTests"/> uses, for the same
    /// reason — high display scaling is ordinary for users of this application.
    /// </summary>
    private const double NarrowDisplayWidthDip = 800;

    /// <summary>The content column that display produces, and the number this work must not move.</summary>
    private const double ExpectedContentColumn = 416.0;

    private static XElement Xml()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml");
        var root = XDocument.Parse(File.ReadAllText(path)).Root;
        Check.WithCustomMessage("SettingsWindow.xaml has no root element.").That(root).IsNotNull();
        return root!;
    }

    private static XElement Named(XElement root, string name)
    {
        var hit = root.DescendantsAndSelf()
            .SingleOrDefault(e => (string?)e.Attribute(Xaml + "Name") == name);
        Check.WithCustomMessage(
                $"SettingsWindow.xaml no longer has an element named \"{name}\".")
            .That(hit).IsNotNull();
        return hit!;
    }

    /// <summary>Reads a Thickness-valued attribute, or <c>default</c> when the attribute is absent.</summary>
    /// <remarks>
    /// A <c>{StaticResource}</c> value cannot be converted here (no resource dictionary is
    /// loaded), so it is reported as the token's own value. That is the point: the guard needs to
    /// know how much the attribute DEFLATES by, and every thickness in this file comes from
    /// <see cref="Tokens.BorderThicknessStandard"/>.
    /// </remarks>
    private static Thickness ThicknessAttr(XElement element, string attribute)
    {
        var attr = element.Attribute(attribute);
        if (attr is null) return default;
        if (attr.Value.Contains("StaticResource", StringComparison.Ordinal))
            return new Thickness(Tokens.BorderThicknessStandard);
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr.Value)!;
    }

    private static Thickness PaddingOf(XElement element)
    {
        var attr = element.Attribute("Padding");
        Check.WithCustomMessage(
                "ContentCard no longer declares a Padding. That padding is the inner inset of the "
                + "content-column arithmetic and SettingsWindow.ContentAvailableWidth reads it at "
                + "runtime.")
            .That(attr).IsNotNull();
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr!.Value)!;
    }

    private static double SidebarWidth(XElement root)
    {
        var columns = Named(root, "RootGrid").Attribute("ColumnDefinitions");
        Check.WithCustomMessage("RootGrid no longer carries a ColumnDefinitions attribute.")
            .That(columns).IsNotNull();
        var first = columns!.Value.Split(',')[0].Trim();
        return double.Parse(first, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static Thickness MarginOf(XElement root, string name)
    {
        var attr = Named(root, name).Attribute("Margin");
        Check.WithCustomMessage($"SettingsWindow.xaml's \"{name}\" declares no Margin.")
            .That(attr).IsNotNull();
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr!.Value)!;
    }

    /// <summary>
    /// Rebuilds SettingsWindow.xaml's content area from the LIVE XAML and arranges it, returning
    /// the width the content panel actually gets.
    ///
    /// <para>
    /// Every inset comes off the file, including <c>ContentCard</c>'s own <c>BorderThickness</c> —
    /// which is <c>default</c> today and must stay so. Reading it rather than assuming zero is
    /// what makes this fail if the outline is ever "tidied" onto the card: the parsed thickness
    /// deflates the panel, the derived budget does not, and the two stop agreeing.
    /// </para>
    /// </summary>
    /// <param name="windowWidth">The window's width in DIP.</param>
    /// <param name="withOutline">False to omit the outline sibling entirely, for the A/B pair.</param>
    /// <returns>The arranged width of ContentPanel, and the derived content-column budget.</returns>
    private static (double ArrangedPanelWidth, double DerivedBudget) MeasureContentPanel(
        double windowWidth, bool withOutline)
    {
        const double windowHeight = 900;
        var root = Xml();
        var cardXml = Named(root, "ContentCard");
        var outlineXml = Named(root, "ContentCardOutline");

        var sidebar = SidebarWidth(root);
        var areaMargin = MarginOf(root, "ContentArea");
        var cardPadding = PaddingOf(cardXml);

        var outer = new Grid { Width = windowWidth, Height = windowHeight };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sidebar) });
        outer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var contentArea = new Grid { Margin = areaMargin };
        contentArea.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(contentArea, 1);
        outer.Children.Add(contentArea);

        // The wrapping Grid that holds the card and its outline as siblings. VerticalAlignment
        // moved here from the card when the wrapper was introduced.
        var wrapper = new Grid { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(wrapper, 0);
        contentArea.Children.Add(wrapper);

        var card = new Border
        {
            Padding = cardPadding,
            BorderThickness = ThicknessAttr(cardXml, "BorderThickness")
        };
        var panel = new StackPanel();
        card.Child = panel;
        wrapper.Children.Add(card);

        if (withOutline)
        {
            wrapper.Children.Add(new Border
            {
                BorderThickness = ThicknessAttr(outlineXml, "BorderThickness"),
                IsHitTestVisible = false
            });
        }

        outer.Measure(new Size(windowWidth, windowHeight));
        outer.Arrange(new Rect(0, 0, windowWidth, windowHeight));
        outer.UpdateLayout();

        var derived = windowWidth - sidebar
            - areaMargin.Left - areaMargin.Right
            - cardPadding.Left - cardPadding.Right;

        return (panel.ActualWidth, derived);
    }

    /// <summary>
    /// The outline costs the content column nothing — the whole reason it is a sibling.
    ///
    /// <para>
    /// Three assertions, and the middle one is the one that bites. The DERIVED budget (what
    /// <c>ContentAvailableWidth</c> computes: window minus sidebar minus both nested insets) must
    /// still be 416 on the narrow display. The ARRANGED panel must actually BE that wide — which
    /// is what an outline moved onto the card breaks, because a Border deflates its child while
    /// the derivation, reading only the Padding, does not notice. And the panel must measure the
    /// same with the outline present as without it.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheOutlineConsumesNoLayout()
    {
        var windowWidth = NarrowDisplayWidthDip * SettingsWindow.WindowSizeRatio;
        var (withOutline, derived) = MeasureContentPanel(windowWidth, withOutline: true);
        var (withoutOutline, _) = MeasureContentPanel(windowWidth, withOutline: false);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The content column derived from SettingsWindow.xaml is now {0:F2} DIP, not the "
                + "{1:F2} the fit suite is pinned to. Something in the sidebar width, "
                + "ContentArea's margin or ContentCard's padding moved; the card's boundary work "
                + "was not supposed to touch any of them.",
                derived, ExpectedContentColumn))
            .That(derived).IsEqualTo(ExpectedContentColumn);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "ContentPanel is arranged {0:F2} DIP wide while ContentAvailableWidth computes "
                + "{1:F2} — a {2:F2} DIP discrepancy. The near-certain cause is a BorderThickness "
                + "written on ContentCard itself: a Border DEFLATES its child by its thickness, "
                + "and ContentAvailableWidth reads only the Padding, so production keeps handing "
                + "controls a budget the card cannot honour and the quick-action picker button is "
                + "over-capped by exactly this much. The card's boundary belongs on the "
                + "ContentCardOutline sibling, which is IsHitTestVisible=False in a wrapping Grid "
                + "and consumes no layout at all — the same construction, for the same reason, as "
                + "DwellToggle's _cardOutline. Do not compensate with a negative margin: on "
                + "DwellToggle that pushed the content OUTSIDE the border line.",
                withOutline, derived, derived - withOutline))
            .That(withOutline).IsEqualTo(derived);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "ContentPanel is {0:F2} DIP wide with the outline and {1:F2} without it, so the "
                + "outline is consuming layout. It is decoration drawn over the card's own edge; "
                + "it must never take room from the rows.",
                withOutline, withoutOutline))
            .That(withOutline).IsEqualTo(withoutOutline);
    }

    /// <summary>Every registered theme.</summary>
    public static TheoryData<ITheme> EveryTheme()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All) data.Add(theme);
        return data;
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// The card is actually perceptible: its boundary clears 3:1 against the page it sits on, in
    /// every registered theme.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void TheCardsBoundary_IsPerceptibleAgainstThePage_InEveryTheme(ITheme theme)
    {
        var ratio = Contrast(theme.SurfaceDark, theme.BorderSubtle);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme the settings content card's boundary reads {1:F2}:1 against the "
                + "window behind it, below the {2:F1} WCAG 1.4.11 floor for a non-text component "
                + "boundary. Without it the card has no edge and the settings rows read as items "
                + "floating on the window, which is the defect this element was added to fix — the "
                + "card's own SurfaceMedium fill is only a {3:F2}:1 step against the same page, "
                + "which is why the boundary is on the border and not the fill (palette rule 1: "
                + "the border carries the boundary, the fill only suggests depth). Brighten the "
                + "theme's BorderSubtle; do not move the boundary back onto the fill.",
                theme.Name, ratio, ComponentBoundaryFloor,
                Contrast(theme.SurfaceDark, theme.SurfaceMedium)))
            .That(ratio).IsGreaterOrEqualThan(ComponentBoundaryFloor);
    }

    /// <summary>
    /// The negative half, and the reason the card's own fill cannot carry its boundary: asserted
    /// as a FACT about the palette, so if a theme is ever restyled such that SurfaceMedium on
    /// SurfaceDark clears 3:1 on its own, this fails and the choice can be revisited deliberately
    /// instead of remaining a stale comment.
    /// </summary>
    [Fact]
    public void TheCardsFill_CannotCarryTheBoundary_WhichIsWhyTheOutlineExists()
    {
        var failing = ThemeRegistry.All
            .Where(t => Contrast(t.SurfaceDark, t.SurfaceMedium) < ComponentBoundaryFloor)
            .Select(t => string.Format(CultureInfo.InvariantCulture,
                "{0} {1:F2}", t.Name, Contrast(t.SurfaceDark, t.SurfaceMedium)))
            .ToList();

        Check.WithCustomMessage(
                "SurfaceMedium on SurfaceDark now clears 3:1 in every theme, so a filled card "
                + "would have a visible edge on its own. ContentCardOutline exists because it did "
                + "not (Midnight measured 1.23:1). If the palette has really moved, revisit that "
                + "deliberately — and update the comment in SettingsWindow.xaml rather than "
                + "deleting this test.")
            .That(failing).Not.IsEmpty();
    }

    /// <summary>
    /// The element is still an outline. Both attributes are needed and neither is inferable from
    /// the other: a brush with no thickness draws nothing, and a thickness with no brush draws
    /// nothing either. Losing one is a silent revert to the invisible card.
    ///
    /// <para>
    /// That the two keys RESOLVE is covered by
    /// <c>OverlayResourceResolutionTests.SettingsWindowXaml_ResolvesEveryStaticResource</c>, which
    /// parses this file against the real merged token dictionary. This checks only that the
    /// attributes are there and are token references — the thing a "tidy-up" removes.
    /// </para>
    /// </summary>
    /// <summary>
    /// The card must carry NO fill of its own, and this is the assertion that keeps it that way.
    ///
    /// <para>
    /// Every control dropped onto this card fills itself with <c>SurfaceMedium</c>:
    /// <c>DwellSlider</c>'s steppers and value plate, <c>DwellComboBox</c>'s options,
    /// <c>DwellColorPicker</c>'s swatch buttons, and <c>SettingsWindow</c>'s own <c>BtnBase</c>.
    /// The card originally shipped filled with that same <c>SurfaceMedium</c>, which put every
    /// one of them at <b>1.00:1 against its own backdrop</b> — measured off a render as
    /// <c>#2A2A44</c> on <c>#292944</c>, one value apart in a single channel. A 100 DIP stepper
    /// with no visible extent leaves the user aiming at a bare glyph, and live testing on the
    /// tracker reported exactly that: "too small / hard to aim at".
    /// </para>
    /// <para>
    /// Unfilled, those controls sit on the page's <c>SurfaceDark</c> and get their designed
    /// 1.23:1 step back. That is still weak — it is the reason the steppers were never easy to
    /// aim at — but it is a separate, pre-existing problem, and it is not this card's to create.
    /// <c>ContentCardOutline</c> carries the boundary, which is where palette rule 1
    /// puts it: the border carries the boundary, the fill only suggests depth. Here the fill was
    /// not suggesting depth — it was cancelling its children's.
    /// </para>
    /// </summary>
    [Fact]
    public void TheCard_CarriesNoFillOfItsOwn()
    {
        var card = Named(Xml(), "ContentCard");
        var background = card.Attribute("Background");

        Check.WithCustomMessage(
                $"ContentCard declares Background=\"{background?.Value}\". Every control on this "
                + "card fills itself with SurfaceMedium, so a filled card collapses them to "
                + "1.00:1 against their own backdrop and the 100 DIP stepper targets lose their "
                + "visible extent entirely. Leave the card unfilled and let ContentCardOutline "
                + "carry the boundary. If a fill is genuinely wanted, it must differ from the "
                + "control fills by enough to keep a control's edge visible — measure it, and "
                + "re-point this test at that measurement rather than deleting it.")
            .That(background).IsNull();
    }

    [Fact]
    public void TheOutline_StillNamesABrushAndAThickness()
    {
        var outline = Named(Xml(), "ContentCardOutline");

        foreach (var name in new[] { "BorderBrush", "BorderThickness" })
        {
            var attr = outline.Attribute(name);
            Check.WithCustomMessage(
                    $"ContentCardOutline no longer declares a {name}, so it draws nothing and the "
                    + "content card is back to having no visible boundary — a 1.23:1 fill step "
                    + "against the page. Both a brush and a thickness are required; neither "
                    + "implies the other.")
                .That(attr).IsNotNull();

            Check.WithCustomMessage(
                    $"ContentCardOutline's {name} is \"{attr!.Value}\", not a token reference. "
                    + "Overlay colours and thicknesses come from the theme dictionary so the "
                    + "theme picker moves them; a literal here would stay on one theme's value.")
                .That(attr.Value).Contains("StaticResource");
        }

        // The LINK between this element and the per-theme gate above, and it is load-bearing:
        // TheCardsBoundary_IsPerceptibleAgainstThePage_InEveryTheme measures theme.BorderSubtle
        // against theme.SurfaceDark. If the outline were repointed at another key — the card's own
        // BrushSurfaceMedium, say, which is the 1.23:1 step this whole element exists to escape —
        // that gate would keep measuring BorderSubtle and keep passing while the card went
        // invisible again. So the key is pinned by name here.
        Check.WithCustomMessage(
                $"ContentCardOutline's BorderBrush is \"{outline.Attribute("BorderBrush")!.Value}\""
                + ", not BrushBorderSubtle. The per-theme perceptibility gate in this file measures "
                + "BorderSubtle against SurfaceDark, so pointing the outline at any other token "
                + "leaves that gate measuring a pairing the page does not use — it would stay green "
                + "over an invisible card. Repoint the gate too if this is deliberate.")
            .That(outline.Attribute("BorderBrush")!.Value).Contains("BrushBorderSubtle");

        Check.WithCustomMessage(
                "ContentCardOutline is hit-testable, so it sits between the user and the rows it "
                + "is painted over. It is decoration; it must never take a gaze click.")
            .That((string?)outline.Attribute("IsHitTestVisible")).IsEqualTo("False");

        Check.WithCustomMessage(
                "ContentCardOutline declares a Width or a Height. It has to STRETCH to the card "
                + "it outlines — an explicit size draws a box that is not the card's box.")
            .That(outline.Attribute("Width") is null && outline.Attribute("Height") is null)
            .IsTrue();
    }
}
