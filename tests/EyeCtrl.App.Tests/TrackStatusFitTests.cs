namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// TrackStatusWindow is the only window whose ramp-affected content overflows its fixed size,
/// and the 12px ramp floor is what pushed it over (design spec §8.6): measured content height
/// 95.88px against the 94px its Padding="8" leaves inside a 110px window.
///
/// This couples the window's height to the arithmetic, so the exception cannot silently rot if
/// the ramp floor or the row structure changes. **Every input is read live**, none is a literal:
/// the ramp floor from <see cref="Tokens.FontSizeBody"/>, the line-height ratio from the very
/// <see cref="FontFamily"/> object <c>OverlayWindow</c> assigns, and the window height, the
/// border padding, the canvas height, every row's vertical margin and the row *count* from
/// TrackStatusWindow.xaml itself.
///
/// **Reading the row structure rather than hard-coding it is the point, and it was a real hole.**
/// An earlier version hard-coded Padding, CanvasHeight, two margins and the row count, so adding
/// a fifth 12px content row added 15.96px of real content while the test kept computing 95.88 and
/// passing — the window would clip with the guard green. Likewise the line-height ratio was the
/// literal 1.33, so retargeting <see cref="Tokens.FontFamilyStandard"/> at a family whose
/// <c>LineSpacing</c> is 1.45 took real content to about 100.20px against the 100.00 available
/// and the test still computed 95.88. Both classes of change now fail loudly instead.
/// </summary>
public class TrackStatusFitTests
{
    /// <summary>The WPF presentation namespace every element in the XAML lives in.</summary>
    private static readonly XNamespace Wpf =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// The line box a single-line <c>TextBlock</c> occupies, as a multiple of its font size.
    ///
    /// Read from <see cref="Tokens.FontStandard"/> — the same cached <see cref="FontFamily"/>
    /// object <c>OverlayWindow</c> assigns to every overlay (OverlayWindow.cs), so this cannot
    /// disagree with what TrackStatusWindow actually renders with. It was a hard-coded 1.33
    /// before, which made a font swap invisible to this test.
    ///
    /// Measured on this machine: the active chain <c>"Segoe UI Variable Text, Segoe UI"</c>,
    /// <see cref="Tokens.FontStandard"/>, and each name in the chain individually all report
    /// <c>LineSpacing</c> = <c>1.330078125</c>, so the derived value reproduces the literal the
    /// spec arithmetic was written against (95.88px of content at the 12px floor). Constructing a
    /// <see cref="FontFamily"/> and reading <c>LineSpacing</c> was verified to work on the plain
    /// <c>[Fact]</c> worker thread (MTA) — no <c>[StaFact]</c> is needed.
    /// </summary>
    /// <returns>The line-height ratio of the font the overlays actually use.</returns>
    private static double LineHeightRatio() => Tokens.FontStandard.LineSpacing;

    /// <summary>
    /// The root element's <c>Height</c>, parsed invariantly.
    ///
    /// Extracted as a method taking XAML text so
    /// <see cref="TheHeightReading_TakesTheRootElementNotANeighbouringConstraint"/> can probe it
    /// directly. It replaces <c>Regex.Match(xaml, "Height=\"(?&lt;h&gt;[\\d.]+)\"")</c>, which was
    /// unanchored, had no left boundary and took the first match in a file holding five
    /// <c>Height=</c> attributes — so only document order made it read the right one.
    /// </summary>
    /// <param name="xaml">The XAML document text.</param>
    /// <returns>The root element's Height in device-independent pixels.</returns>
    private static double RootHeight(string xaml)
    {
        var root = XDocument.Parse(xaml).Root;
        Check.WithCustomMessage("The XAML has no root element.").That(root).IsNotNull();
        return Px(Attr(root!, "Height"));
    }

    /// <summary>Parses a XAML length invariantly, so a comma decimal separator cannot creep in.</summary>
    /// <param name="value">The attribute text.</param>
    /// <returns>The parsed length.</returns>
    private static double Px(string value) =>
        double.Parse(value.Trim(), CultureInfo.InvariantCulture);

    /// <summary>
    /// An attribute's value, failing loudly rather than throwing a bare
    /// <c>NullReferenceException</c> if the XAML no longer carries it.
    /// </summary>
    /// <param name="element">The element to read.</param>
    /// <param name="name">The unprefixed attribute name.</param>
    /// <returns>The attribute value.</returns>
    private static string Attr(XElement element, string name)
    {
        var attribute = element.Attribute(name);
        Check.WithCustomMessage(
                $"<{element.Name.LocalName}> in TrackStatusWindow.xaml no longer carries a "
                + $"{name} attribute, which this fit arithmetic reads. The layout changed: redo "
                + "the extraction and the arithmetic together.")
            .That(attribute).IsNotNull();
        return attribute!.Value;
    }

    /// <summary>
    /// The vertical space an element's <c>Margin</c> adds: top plus bottom, or 0 when it carries
    /// no margin at all.
    ///
    /// Read per row rather than hard-coded so that a margin *added* to a row that has none today
    /// — the Quality row, for instance — is counted instead of silently ignored.
    /// </summary>
    /// <param name="element">The row to measure.</param>
    /// <returns>Top + bottom margin in device-independent pixels.</returns>
    private static double VerticalMargin(XElement element)
    {
        var margin = element.Attribute("Margin");
        if (margin is null) return 0;

        var parts = margin.Value.Split(',');
        Check.WithCustomMessage(
                $"<{element.Name.LocalName}> has Margin=\"{margin.Value}\", which is not the "
                + "four-part left,top,right,bottom form this arithmetic reads. A uniform or "
                + "two-part margin means different vertical space: rewrite VerticalMargin.")
            .That(parts.Length).IsEqualTo(4);

        return Px(parts[1]) + Px(parts[3]);
    }

    [Fact]
    public void TheWindowIsTallEnoughForTheRampFloor()
    {
        var xaml = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TrackStatusWindow.xaml"));

        var height = RootHeight(xaml);
        var border = XDocument.Parse(xaml).Root!.Element(Wpf + "Border");
        Check.WithCustomMessage(
                "TrackStatusWindow.xaml's root no longer holds a <Border> directly. Its padding "
                + "is what this test subtracts from the window height; redo the extraction.")
            .That(border).IsNotNull();

        var stack = border!.Element(Wpf + "StackPanel");
        Check.WithCustomMessage(
                "The <Border> no longer wraps a content <StackPanel>. The rows this test sums "
                + "come from it; redo the extraction.")
            .That(stack).IsNotNull();

        // The row count and each row's kind, not just its numbers. Hard-coding "four rows" is how
        // a fifth row would have slipped through: it adds a full line box of real content that a
        // fixed sum never sees.
        var rows = stack!.Elements().ToList();
        Check.WithCustomMessage(
                "TrackStatusWindow's content rows are now "
                + $"[{string.Join(", ", rows.Select(r => r.Name.LocalName))}], not the four this "
                + "fit arithmetic sums (header TextBlock, eye-dot Canvas, Distance row, Quality "
                + "row). A structural change moves the content height, so redo the arithmetic "
                + "below for the new structure — do not just relax this assertion.")
            .That(rows.Select(r => r.Name.LocalName))
            .ContainsExactly("TextBlock", "Canvas", "StackPanel", "StackPanel");

        var padding = Px(Attr(border, "Padding"));       // Border Padding="8", top and bottom
        var headerMargin = VerticalMargin(rows[0]);      // "Track Status"
        var canvasHeight = Px(Attr(rows[1], "Height"));  // the eye-dot track box
        var canvasMargin = VerticalMargin(rows[1]);
        var distanceRowMargin = VerticalMargin(rows[2]);
        var qualityRowMargin = VerticalMargin(rows[3]);

        var line = Tokens.FontSizeBody * LineHeightRatio();
        var content = (line + headerMargin)                   // "Track Status"
                    + (canvasHeight + canvasMargin)           // eye-dot canvas
                    + (line + distanceRowMargin)              // Distance row
                    + (line + qualityRowMargin);              // Quality row

        var available = height - (2 * padding);

        // Invariant formatting throughout: the German default printed "95,88px" and "94,00",
        // so the figures this message documents could not be grepped for.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "TrackStatusWindow needs {0:F2}px of content space but Height={1} leaves only "
                + "{2:F2}. At the 12px ramp floor the bottom row clips, cutting the descender "
                + "from \"Quality\" and its value. Design spec §8.6 sets Height=116; if the ramp "
                + "floor or the row structure changed, redo that arithmetic rather than "
                + "heightening the window again. (Width was deliberately left at 160: the widest "
                + "row measures 94.45px inside a 144px box, so the overflow is vertical only.)",
                content, height, available))
            .That(available).IsGreaterOrEqualThan(content);
    }

    [Fact]
    public void TheHeightReading_TakesTheRootElementNotANeighbouringConstraint()
    {
        // The regex this replaced was unanchored, first-match-wins and had no left boundary, so
        // any Height-suffixed attribute placed before Height on the root element captured
        // instead. Both probes below returned the wrong number under it and would have made
        // TheWindowIsTallEnoughForTheRampFloor pass while the window clipped: 200 is generous
        // enough to hide any overflow, and 99 fails the comparison for the wrong reason.
        var probes = new Dictionary<string, string>
        {
            ["MinHeight before Height"] =
                @"<overlays:OverlayWindow xmlns:overlays=""urn:o"" MinHeight=""200"" Height=""116""/>",
            ["MaxHeight and Width before Height"] =
                @"<overlays:OverlayWindow xmlns:overlays=""urn:o"" MaxHeight=""99"" Width=""160"" Height=""116""/>",
        };

        foreach (var (label, probe) in probes)
            Check.WithCustomMessage(
                    $"RootHeight read a neighbouring constraint instead of Height ({label}). "
                    + "That is exactly the silent-pass hole the regex had.")
                .That(RootHeight(probe)).IsEqualTo(116d);
    }
}
